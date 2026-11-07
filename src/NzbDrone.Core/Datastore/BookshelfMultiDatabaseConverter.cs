using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace NzbDrone.Core.Datastore
{
    /// <summary>
    /// Merges two BookshelfNG SQLite databases for normal automatic startup or
    /// the explicit preview/apply utility. Original databases are never modified.
    /// Conflicting IDs and unique settings stop before a native database is promoted.
    /// </summary>
    public sealed class BookshelfMultiDatabaseConverter
    {
        private const string ReadarrDatabaseName = "readarr.db";
        private const string AudioarrDatabaseName = "audioarr.db";
        private const string MergeReceiptName = "bookshelfng-merge.receipt";
        private const string MergeMediaMapName = "bookshelfng-merge-media-map.json";
        private static readonly HashSet<string> ResetOnImportTables = new(StringComparer.Ordinal)
        {
            "Commands", "DownloadClientStatus", "ImportListStatus", "IndexerStatus",
            "NotificationStatus", "PendingReleases", "ScheduledTasks", "VersionInfo"
        };

        public BookshelfDatabaseConversionResult Convert(
            string primaryDatabasePath,
            string appDataFolder,
            Action<string> migrateNativeTarget,
            Func<SqliteConnection, SqliteConnection, SqliteTransaction, int, string, BookshelfDatabaseImportReport> importRows,
            bool previewOnly = false,
            Action<string> migrateBookshelfSource = null,
            Action<string, string> reportProgress = null,
            Action<string, string> reportOutcome = null)
        {
            if (migrateNativeTarget == null) throw new ArgumentNullException(nameof(migrateNativeTarget));
            if (importRows == null) throw new ArgumentNullException(nameof(importRows));
            if (string.IsNullOrWhiteSpace(appDataFolder)) throw new ArgumentException("An app-data directory is required.", nameof(appDataFolder));

            appDataFolder = Path.GetFullPath(appDataFolder);
            primaryDatabasePath = Path.GetFullPath(primaryDatabasePath);
            var readarrPath = Path.Combine(appDataFolder, ReadarrDatabaseName);
            var audioarrPath = Path.Combine(appDataFolder, AudioarrDatabaseName);
            var nativePath = Path.Combine(appDataFolder, "chaptarr.db");
            var receiptPath = Path.Combine(appDataFolder, MergeReceiptName);
            if (!PathEquals(primaryDatabasePath, readarrPath))
            {
                throw new InvalidDataException("The BookshelfNG multi-database merge uses readarr.db as the stable primary source. No database was changed.");
            }

            EnsureRegularDatabaseFamily(readarrPath, ReadarrDatabaseName);
            EnsureRegularDatabaseFamily(audioarrPath, AudioarrDatabaseName);
            if (!HasRecoverableSqliteData(readarrPath) || !HasRecoverableSqliteData(audioarrPath))
            {
                throw new InvalidDataException("The multi-database merge requires both regular BookshelfNG database files to contain recoverable SQLite data. No database was changed.");
            }

            if (DatabaseLineageGuard.Detect(ConnectionString(readarrPath), DatabaseType.SQLite) != DatabaseLineage.BookshelfNG ||
                DatabaseLineageGuard.Detect(ConnectionString(audioarrPath), DatabaseType.SQLite) != DatabaseLineage.BookshelfNG)
            {
                throw new InvalidDataException("Both readarr.db and audioarr.db must use a supported BookshelfNG schema. No database was changed.");
            }

            EnsureTargetPath(nativePath, allowEmptyPlaceholder: true);
            EnsureReceiptPath(receiptPath);

            var readarrFingerprint = BookshelfMergeReceipt.FingerprintDatabaseFamily(readarrPath);
            var audioarrFingerprint = BookshelfMergeReceipt.FingerprintDatabaseFamily(audioarrPath);
            var mediaMapFingerprints = CaptureMediaMapFingerprints(appDataFolder, readarrPath, audioarrPath);
            var lockPath = Path.Combine(appDataFolder, "bookshelfng-multi-database-merge.lock");
            using var conversionLock = AcquireLock(lockPath);
            using var primaryConversionLock = AcquireLock(readarrPath + ".bookshelfng-conversion.lock");
            using var secondaryConversionLock = AcquireLock(audioarrPath + ".bookshelfng-conversion.lock");
            EnsureConversionSpace(appDataFolder, readarrPath, audioarrPath);

            var workDirectory = Path.Combine(appDataFolder, ".chaptarr-bookshelf-merge-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(workDirectory);
            RestrictDirectory(workDirectory);
            var primarySnapshotPath = Path.Combine(workDirectory, ReadarrDatabaseName);
            var secondarySnapshotPath = Path.Combine(workDirectory, AudioarrDatabaseName);
            var mergedSourcePath = Path.Combine(workDirectory, "merged-bookshelf.db");
            var mergeCounts = new Dictionary<string, (long Inserted, long Identical)>(StringComparer.Ordinal);
            var completed = false;
            string retainedBackupPath = null;

            try
            {
                reportProgress?.Invoke("merge-snapshotting", ReadarrDatabaseName + "," + AudioarrDatabaseName);
                CreateConsistentSnapshot(readarrPath, primarySnapshotPath);
                CreateConsistentSnapshot(audioarrPath, secondarySnapshotPath);
                EnsureSourcesUnchanged(readarrPath, audioarrPath, readarrFingerprint, audioarrFingerprint);
                EnsureMediaMapsUnchanged(mediaMapFingerprints);

                MigrateSourceCopy(primarySnapshotPath, migrateBookshelfSource);
                MigrateSourceCopy(secondarySnapshotPath, migrateBookshelfSource);
                mergeCounts = MergeSources(primarySnapshotPath, secondarySnapshotPath, mergedSourcePath);
                WriteMergedMediaMap(appDataFolder, readarrPath, audioarrPath, mergedSourcePath);
                reportOutcome?.Invoke("merge-table-counts", FormatCounts(mergeCounts));

                var conversion = new BookshelfDatabaseConverter().ConvertWithSourceSchema(
                    ConnectionString(mergedSourcePath),
                    migrateNativeTarget,
                    (source, target, transaction, schemaVersion) => importRows(
                        source,
                        target,
                        transaction,
                        schemaVersion,
                        mergedSourcePath),
                    previewOnly,
                    migrateBookshelfSource,
                    reportProgress,
                    reportOutcome);

                EnsureSourcesUnchanged(readarrPath, audioarrPath, readarrFingerprint, audioarrFingerprint);
                EnsureMediaMapsUnchanged(mediaMapFingerprints);
                PreserveMediaReview(workDirectory, appDataFolder, reportOutcome);
                if (previewOnly)
                {
                    completed = true;
                    return conversion;
                }

                conversion.ImportReport.ThrowIfBlockingIssues();
                EnsureSourcesUnchanged(readarrPath, audioarrPath, readarrFingerprint, audioarrFingerprint);
                EnsureMediaMapsUnchanged(mediaMapFingerprints);
                EnsureTargetPath(nativePath, allowEmptyPlaceholder: true);

                var timestamp = DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffZ", System.Globalization.CultureInfo.InvariantCulture);
                retainedBackupPath = Path.Combine(appDataFolder, "chaptarr.db.bookshelfng-backup-" + timestamp + "-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".db");
                File.Move(conversion.BackupPath, retainedBackupPath);
                RestrictFile(retainedBackupPath);
                RecordMergeSourceFingerprints(reportOutcome, readarrPath, audioarrPath);

                var targetFingerprint = BookshelfMigrationJournal.ComputeFingerprint(mergedSourcePath);
                var receipt = new BookshelfMergeReceipt(readarrFingerprint, audioarrFingerprint, targetFingerprint, "prepared");
                receipt.Write(receiptPath);

                PreserveEmptyTargetPlaceholder(nativePath);
                if (DatabaseLineageGuard.Detect(ConnectionString(mergedSourcePath), DatabaseType.SQLite) != DatabaseLineage.Chaptarr)
                {
                    throw new InvalidDataException("The merged staged database did not validate as a native ChaptarrNG database. The original databases were not changed.");
                }

                EnsureSourcesUnchanged(readarrPath, audioarrPath, readarrFingerprint, audioarrFingerprint);
                EnsureMediaMapsUnchanged(mediaMapFingerprints);
                RestrictFile(mergedSourcePath);
                File.Move(mergedSourcePath, nativePath);
                try
                {
                    receipt.WithState("complete").Write(receiptPath);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    // The prepared receipt is sufficient for startup reconciliation.
                    ReportOutcomeSafely(reportOutcome, "merge-receipt-finalization", "prepared-receipt-retained:" + exception.GetType().Name);
                }

                completed = true;
                ReportOutcomeSafely(reportOutcome, "merge-native-database", Path.GetFileName(nativePath));
                ReportOutcomeSafely(reportOutcome, "merge-original-sources", ReadarrDatabaseName + "," + AudioarrDatabaseName + ":retained");
                return new BookshelfDatabaseConversionResult(retainedBackupPath, conversion.ImportReport, previewOnly: false);
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or SqliteException or InvalidOperationException)
            {
                throw new InvalidOperationException(
                    $"BookshelfNG multi-database migration stopped without changing either original source. Review the preserved work directory '{workDirectory}' before retrying.",
                    exception);
            }
            finally
            {
                if (completed)
                {
                    try
                    {
                        Directory.Delete(workDirectory, recursive: true);
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                    {
                        ReportOutcomeSafely(reportOutcome, "merge-work-cleanup", "retained:" + Path.GetFileName(workDirectory));
                    }
                }
            }
        }

        private static Dictionary<string, (long Inserted, long Identical)> MergeSources(
            string primarySnapshotPath,
            string secondarySnapshotPath,
            string mergedSourcePath)
        {
            // Source migrations may leave committed pages in a WAL. A SQLite
            // backup includes those pages; copying only the main file could drop
            // them from the merged source.
            CreateConsistentSnapshot(primarySnapshotPath, mergedSourcePath);
            using var primary = Open(mergedSourcePath, SqliteOpenMode.ReadWrite);
            using var secondary = Open(secondarySnapshotPath, SqliteOpenMode.ReadOnly);
            var primaryTables = GetTables(primary);
            var secondaryTables = GetTables(secondary);
            if (!primaryTables.SequenceEqual(secondaryTables, StringComparer.Ordinal))
            {
                throw new InvalidDataException("The migrated BookshelfNG databases do not have identical table sets. No database was changed.");
            }

            BookshelfDatabaseImporter.ValidateSourceSchema(primary);
            BookshelfDatabaseImporter.ValidateSourceSchema(secondary);

            var counts = new Dictionary<string, (long Inserted, long Identical)>(StringComparer.Ordinal);
            using (var enableForeignKeys = primary.CreateCommand())
            {
                enableForeignKeys.CommandText = "PRAGMA foreign_keys = ON;";
                enableForeignKeys.ExecuteNonQuery();
            }

            using var transaction = primary.BeginTransaction();
            using (var defer = primary.CreateCommand())
            {
                defer.Transaction = transaction;
                defer.CommandText = "PRAGMA defer_foreign_keys = ON;";
                defer.ExecuteNonQuery();
            }

            foreach (var table in primaryTables)
            {
                if (ResetOnImportTables.Contains(table))
                {
                    counts[table] = (0, CountRows(secondary, table));
                    continue;
                }

                var primaryColumns = GetColumns(primary, table);
                var secondaryColumns = GetColumns(secondary, table);
                if (!primaryColumns.Select(column => (column.Name, column.Type, column.PrimaryKeyOrder))
                    .SequenceEqual(secondaryColumns.Select(column => (column.Name, column.Type, column.PrimaryKeyOrder))))
                {
                    throw new InvalidDataException($"BookshelfNG table '{table}' has incompatible source schemas. No database was changed.");
                }

                var primaryKeys = primaryColumns.Where(column => column.PrimaryKeyOrder > 0)
                    .OrderBy(column => column.PrimaryKeyOrder)
                    .ToArray();
                if (primaryKeys.Length == 0)
                {
                    throw new InvalidDataException($"BookshelfNG table '{table}' has no stable primary key, so the two databases cannot be merged without risking duplicate or lost records. No database was changed.");
                }

                long inserted = 0;
                long identical = 0;
                using var rowsCommand = secondary.CreateCommand();
                rowsCommand.CommandText = $"SELECT * FROM {Quote(table)};"; // nosemgrep: csharp.lang.security.sqli.csharp-sqli.csharp-sqli -- table comes from the BookshelfNG schema allowlist and Quote escapes the identifier.
                using var rows = rowsCommand.ExecuteReader();
                while (rows.Read())
                {
                    var values = new object[rows.FieldCount];
                    for (var index = 0; index < rows.FieldCount; index++)
                    {
                        values[index] = rows.IsDBNull(index) ? null : rows.GetValue(index);
                    }

                    if (string.Equals(table, "Config", StringComparison.OrdinalIgnoreCase) &&
                        FindMatchingConfig(primary, transaction, primaryColumns, values, out var conflictingConfig))
                    {
                        if (conflictingConfig)
                        {
                            throw new InvalidDataException("The two BookshelfNG databases contain different values for the same configuration key. Resolve the setting conflict before merging. No database was changed.");
                        }

                        identical++;
                        continue;
                    }

                    var existing = FindByPrimaryKey(primary, transaction, table, primaryColumns, primaryKeys, values);
                    if (existing != null)
                    {
                        if (string.Equals(table, "Config", StringComparison.OrdinalIgnoreCase))
                        {
                            var idIndex = primaryColumns.ToList().FindIndex(column => string.Equals(column.Name, "Id", StringComparison.OrdinalIgnoreCase));
                            if (idIndex < 0)
                            {
                                throw new InvalidDataException("BookshelfNG Config rows have no stable key, so their settings cannot be merged safely. No database was changed.");
                            }

                            values[idIndex] = NextConfigId(primary, transaction);
                        }
                        else
                        {
                            if (!RowsEqual(values, existing))
                            {
                                throw new InvalidDataException($"The two BookshelfNG databases contain different '{table}' records with the same primary identity. Resolve the ID conflict before merging. No database was changed.");
                            }

                            identical++;
                            continue;
                        }
                    }

                    InsertRow(primary, transaction, table, primaryColumns, values);
                    inserted++;
                }

                counts[table] = (inserted, identical);
            }

            try
            {
                transaction.Commit();
            }
            catch (SqliteException exception)
            {
                throw new InvalidDataException("The two BookshelfNG databases contain conflicting unique values or references. Resolve the identity/configuration conflict before merging. No database was changed.", exception);
            }

            return counts;
        }

        private static bool FindMatchingConfig(
            SqliteConnection primary,
            SqliteTransaction transaction,
            IReadOnlyList<SqliteColumn> columns,
            object[] values,
            out bool conflicting)
        {
            conflicting = false;
            var keyIndex = columns.ToList().FindIndex(column => string.Equals(column.Name, "Key", StringComparison.OrdinalIgnoreCase));
            if (keyIndex < 0 || values[keyIndex] == null)
            {
                return false;
            }

            using var command = primary.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"SELECT * FROM {Quote("Config")} WHERE {Quote(columns[keyIndex].Name)} = @key COLLATE NOCASE LIMIT 2;";
            command.Parameters.AddWithValue("@key", values[keyIndex]);
            using var reader = command.ExecuteReader();
            if (!reader.Read()) return false;

            var existing = new object[reader.FieldCount];
            for (var index = 0; index < reader.FieldCount; index++)
            {
                existing[index] = reader.IsDBNull(index) ? null : reader.GetValue(index);
            }

            if (reader.Read())
            {
                conflicting = true;
                return true;
            }

            var idIndex = columns.ToList().FindIndex(column => string.Equals(column.Name, "Id", StringComparison.OrdinalIgnoreCase));
            for (var index = 0; index < values.Length; index++)
            {
                if (index == idIndex)
                {
                    continue;
                }

                var equal = index == keyIndex && values[index] is string left && existing[index] is string right
                    ? string.Equals(left, right, StringComparison.OrdinalIgnoreCase)
                    : ValuesEqual(values[index], existing[index]);
                if (!equal)
                {
                    conflicting = true;
                    break;
                }
            }

            return true;
        }

        private static long NextConfigId(SqliteConnection primary, SqliteTransaction transaction)
        {
            using var command = primary.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT COALESCE(MAX(\"Id\"), 0) + 1 FROM \"Config\";";
            return System.Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        }

        private static object[] FindByPrimaryKey(
            SqliteConnection primary,
            SqliteTransaction transaction,
            string table,
            IReadOnlyList<SqliteColumn> columns,
            IReadOnlyList<SqliteColumn> primaryKeys,
            object[] values)
        {
            var indexes = primaryKeys.Select(key => columns.ToList().FindIndex(column => column.Name == key.Name)).ToArray();
            using var command = primary.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"SELECT * FROM {Quote(table)} WHERE " + string.Join(" AND ", primaryKeys.Select((key, index) => $"{Quote(key.Name)} IS @key{index}")) + " LIMIT 1;"; // nosemgrep: csharp.lang.security.sqli.csharp-sqli.csharp-sqli -- table and primary-key columns come from the validated source schema and Quote escapes each identifier.
            for (var index = 0; index < indexes.Length; index++)
            {
                command.Parameters.AddWithValue("@key" + index, values[indexes[index]] ?? DBNull.Value);
            }

            using var reader = command.ExecuteReader();
            if (!reader.Read()) return null;

            var result = new object[reader.FieldCount];
            for (var index = 0; index < reader.FieldCount; index++)
            {
                result[index] = reader.IsDBNull(index) ? null : reader.GetValue(index);
            }

            return result;
        }

        private static void InsertRow(
            SqliteConnection primary,
            SqliteTransaction transaction,
            string table,
            IReadOnlyList<SqliteColumn> columns,
            object[] values)
        {
            using var command = primary.CreateCommand();
            command.Transaction = transaction;
            // nosemgrep: csharp.lang.security.sqli.csharp-sqli.csharp-sqli -- table and column identifiers come from the validated source schema and Quote escapes them; row values are bound parameters.
            command.CommandText = $"INSERT INTO {Quote(table)} (" + string.Join(",", columns.Select(column => Quote(column.Name))) + ") VALUES (" +
                string.Join(",", columns.Select((_, index) => "@value" + index)) + ");";
            for (var index = 0; index < values.Length; index++)
            {
                command.Parameters.AddWithValue("@value" + index, values[index] ?? DBNull.Value);
            }

            try
            {
                command.ExecuteNonQuery();
            }
            catch (SqliteException exception)
            {
                throw new InvalidDataException($"BookshelfNG table '{table}' has a conflicting unique identity or reference. Resolve the conflict before merging. No database was changed.", exception);
            }
        }

        private static List<SqliteColumn> GetColumns(SqliteConnection connection, string table)
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"PRAGMA table_info({Quote(table)});"; // nosemgrep: csharp.lang.security.sqli.csharp-sqli.csharp-sqli -- table comes from the validated BookshelfNG schema allowlist and Quote escapes the identifier.
            using var reader = command.ExecuteReader();
            var columns = new List<SqliteColumn>();
            while (reader.Read())
            {
                columns.Add(new SqliteColumn(
                    reader.GetString(reader.GetOrdinal("name")),
                    reader.GetString(reader.GetOrdinal("type")),
                    reader.GetInt32(reader.GetOrdinal("pk"))));
            }

            return columns;
        }

        private static List<string> GetTables(SqliteConnection connection)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name;";
            using var reader = command.ExecuteReader();
            var tables = new List<string>();
            while (reader.Read()) tables.Add(reader.GetString(0));
            return tables;
        }

        private static long CountRows(SqliteConnection connection, string table)
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT COUNT(*) FROM {Quote(table)};"; // nosemgrep: csharp.lang.security.sqli.csharp-sqli.csharp-sqli -- table comes from the validated BookshelfNG schema allowlist and Quote escapes the identifier.
            return System.Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        }

        private static bool RowsEqual(IReadOnlyList<object> left, IReadOnlyList<object> right)
        {
            if (left.Count != right.Count) return false;
            for (var index = 0; index < left.Count; index++)
            {
                if (!ValuesEqual(left[index], right[index])) return false;
            }

            return true;
        }

        private static bool ValuesEqual(object left, object right)
        {
            if (left == null || right == null) return left == null && right == null;
            if (left is byte[] leftBytes && right is byte[] rightBytes) return leftBytes.SequenceEqual(rightBytes);
            return Equals(left, right);
        }

        private static void MigrateSourceCopy(string path, Action<string> migrateBookshelfSource)
        {
            if (migrateBookshelfSource == null)
            {
                using var source = Open(path, SqliteOpenMode.ReadOnly);
                BookshelfDatabaseImporter.ValidateSourceSchema(source);
                return;
            }

            migrateBookshelfSource(ConnectionString(path, SqliteOpenMode.ReadWrite));
            using var migrated = Open(path, SqliteOpenMode.ReadOnly);
            BookshelfDatabaseImporter.ValidateSourceSchema(migrated);
        }

        private static void CreateConsistentSnapshot(string sourcePath, string snapshotPath)
        {
            using var source = Open(sourcePath, SqliteOpenMode.ReadOnly);
            using var snapshot = Open(snapshotPath, SqliteOpenMode.ReadWriteCreate);
            source.BackupDatabase(snapshot);
        }

        private static void WriteMergedMediaMap(string appDataFolder, string readarrPath, string audioarrPath, string mergedSourcePath)
        {
            var mergedMapPath = mergedSourcePath + ".bookshelf-media-map.json";
            var explicitMapPath = Path.Combine(appDataFolder, MergeMediaMapName);
            if (File.Exists(explicitMapPath))
            {
                EnsureRegularFile(explicitMapPath, MergeMediaMapName);
                File.Copy(explicitMapPath, mergedMapPath);
                return;
            }

            var sourceMaps = new[]
            {
                readarrPath + ".bookshelf-media-map.json",
                audioarrPath + ".bookshelf-media-map.json"
            }.Where(File.Exists).ToArray();
            if (sourceMaps.Length == 0) return;

            var choices = new SortedDictionary<int, string>();
            foreach (var sourceMapPath in sourceMaps)
            {
                EnsureRegularFile(sourceMapPath, Path.GetFileName(sourceMapPath));
                JObject document;
                try
                {
                    document = JObject.Parse(File.ReadAllText(sourceMapPath));
                }
                catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
                {
                    throw new InvalidDataException($"BookshelfNG media map '{Path.GetFileName(sourceMapPath)}' could not be read. No database was changed.", exception);
                }

                if ((int?)document["version"] != 1 || document["books"] is not JArray books)
                {
                    throw new InvalidDataException($"BookshelfNG media map '{Path.GetFileName(sourceMapPath)}' must contain version 1 and a books array. No database was changed.");
                }

                var seenInSourceMap = new HashSet<int>();
                foreach (var book in books.OfType<JObject>())
                {
                    var id = (int?)book["sourceBookId"];
                    var mediaType = (string)book["mediaType"];
                    if (!id.HasValue || id.Value <= 0 || string.IsNullOrWhiteSpace(mediaType))
                    {
                        throw new InvalidDataException($"BookshelfNG media map '{Path.GetFileName(sourceMapPath)}' contains an invalid book entry. No database was changed.");
                    }

                    if (!seenInSourceMap.Add(id.Value))
                    {
                        throw new InvalidDataException($"BookshelfNG media map '{Path.GetFileName(sourceMapPath)}' contains duplicate source book ID {id.Value}. No database was changed.");
                    }

                    if (choices.TryGetValue(id.Value, out var existing) && !string.Equals(existing, mediaType, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidDataException($"The two BookshelfNG media maps assign different formats to source book ID {id.Value}. Supply '{MergeMediaMapName}' with reviewed choices. No database was changed.");
                    }

                    choices[id.Value] = mediaType;
                }
            }

            var merged = new JObject
            {
                ["version"] = 1,
                ["books"] = new JArray(choices.Select(choice => new JObject
                {
                    ["sourceBookId"] = choice.Key,
                    ["mediaType"] = choice.Value
                }))
            };
            File.WriteAllText(mergedMapPath, merged.ToString(Formatting.Indented), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }

        private static IReadOnlyDictionary<string, string> CaptureMediaMapFingerprints(string appDataFolder, string readarrPath, string audioarrPath)
        {
            var paths = new[]
            {
                readarrPath + ".bookshelf-media-map.json",
                audioarrPath + ".bookshelf-media-map.json",
                Path.Combine(appDataFolder, MergeMediaMapName)
            };
            var fingerprints = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var path in paths)
            {
                EnsureRegularFile(path, Path.GetFileName(path));
                fingerprints[path] = BookshelfMigrationJournal.ComputeFingerprint(path);
            }

            return fingerprints;
        }

        private static void EnsureMediaMapsUnchanged(IReadOnlyDictionary<string, string> fingerprints)
        {
            foreach (var item in fingerprints)
            {
                if (!string.Equals(BookshelfMigrationJournal.ComputeFingerprint(item.Key), item.Value, StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        $"BookshelfNG media-map input '{Path.GetFileName(item.Key)}' changed during the merge. Preserve both databases and media-map files before retrying.");
                }
            }
        }

        private static void PreserveMediaReview(string workDirectory, string appDataFolder, Action<string, string> reportOutcome)
        {
            var review = Path.Combine(workDirectory, "merged-bookshelf.db.bookshelf-media-review.json");
            if (!File.Exists(review)) return;

            var name = "bookshelfng-merge-media-review-" + DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffZ", System.Globalization.CultureInfo.InvariantCulture) + ".json";
            var destination = Path.Combine(appDataFolder, name);
            File.Copy(review, destination);
            RestrictFile(destination);
            reportOutcome?.Invoke("merge-media-review", name);
        }

        private static void RecordMergeSourceFingerprints(Action<string, string> reportOutcome, string readarrPath, string audioarrPath)
        {
            reportOutcome?.Invoke("merge-readarr-fingerprint", BookshelfMergeReceipt.FingerprintDatabaseFamily(readarrPath));
            reportOutcome?.Invoke("merge-audioarr-fingerprint", BookshelfMergeReceipt.FingerprintDatabaseFamily(audioarrPath));
        }

        private static void ReportOutcomeSafely(Action<string, string> reportOutcome, string name, string value)
        {
            try
            {
                reportOutcome?.Invoke(name, value);
            }
            catch
            {
                // Journal delivery after promotion cannot undo the atomic native
                // database commit; the receipt remains the reconciliation source.
            }
        }

        private static string FormatCounts(IReadOnlyDictionary<string, (long Inserted, long Identical)> counts)
        {
            if (counts.Count == 0)
            {
                return "no-tables";
            }

            return string.Join(".", counts.OrderBy(item => item.Key, StringComparer.Ordinal)
                .Select(item => ResetOnImportTables.Contains(item.Key)
                    ? item.Key + ":reset-" + item.Value.Identical
                    : item.Key + ":added-" + item.Value.Inserted + "-identical-" + item.Value.Identical));
        }

        private static void EnsureSourcesUnchanged(string readarrPath, string audioarrPath, string readarrFingerprint, string audioarrFingerprint)
        {
            if (!string.Equals(BookshelfMergeReceipt.FingerprintDatabaseFamily(readarrPath), readarrFingerprint, StringComparison.Ordinal) ||
                !string.Equals(BookshelfMergeReceipt.FingerprintDatabaseFamily(audioarrPath), audioarrFingerprint, StringComparison.Ordinal))
            {
                throw new InvalidDataException("A BookshelfNG source database changed during the merge. Stop all BookshelfNG processes and review both originals before retrying.");
            }
        }

        private static void EnsureRegularDatabaseFamily(string path, string name)
        {
            EnsureRegularFile(path, name);
            foreach (var suffix in new[] { "-wal", "-journal", "-shm" })
            {
                EnsureRegularFile(path + suffix, name + suffix);
            }
        }

        private static bool HasRecoverableSqliteData(string databasePath)
        {
            if (!File.Exists(databasePath))
            {
                return false;
            }

            if (new FileInfo(databasePath).Length > 0)
            {
                return true;
            }

            return new[] { "-journal", "-wal" }
                .Any(suffix => File.Exists(databasePath + suffix) && new FileInfo(databasePath + suffix).Length > 0);
        }

        private static void EnsureRegularFile(string path, string description)
        {
            var file = new FileInfo(path);
            var directory = new DirectoryInfo(path);
            if (file.LinkTarget is not null || directory.LinkTarget is not null)
            {
                throw new InvalidDataException($"The BookshelfNG path '{description}' is a symbolic link. Resolve it to a regular file before merging.");
            }

            if (Directory.Exists(path) || (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0))
            {
                throw new InvalidDataException($"The BookshelfNG path '{description}' is not a regular file. Resolve it before merging.");
            }
        }

        private static void EnsureTargetPath(string path, bool allowEmptyPlaceholder)
        {
            EnsureRegularDatabaseFamily(path, Path.GetFileName(path));
            var hasDurableSidecar = DatabaseLineageGuard.HasNonEmptyDurableSidecar(path);
            if ((!File.Exists(path) && hasDurableSidecar) ||
                (File.Exists(path) && (!allowEmptyPlaceholder || new FileInfo(path).Length > 0 || hasDurableSidecar)))
            {
                throw new InvalidDataException("A ChaptarrNG database already exists beside the two BookshelfNG sources. Resolve it before running the multi-database merge.");
            }
        }

        private static void EnsureReceiptPath(string path)
        {
            EnsureRegularFile(path, MergeReceiptName);
        }

        private static void PreserveEmptyTargetPlaceholder(string path)
        {
            var hasMainFile = File.Exists(path);
            if ((hasMainFile && new FileInfo(path).Length != 0) || DatabaseLineageGuard.HasNonEmptyDurableSidecar(path))
            {
                throw new InvalidDataException("The ChaptarrNG destination changed during merge. No source database was changed.");
            }

            var suffix = ".empty-before-bookshelf-merge-" + Guid.NewGuid().ToString("N");
            if (hasMainFile)
            {
                File.Move(path, path + suffix);
            }

            foreach (var sidecar in new[] { "-wal", "-journal", "-shm" })
            {
                if (File.Exists(path + sidecar))
                {
                    File.Move(path + sidecar, path + sidecar + suffix);
                }
            }
        }

        private static FileStream AcquireLock(string path)
        {
            try
            {
                EnsureRegularFile(path, Path.GetFileName(path));
                var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                if (!OperatingSystem.IsWindows())
                {
                    File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                }

                return stream;
            }
            catch (IOException exception)
            {
                throw new InvalidOperationException("Another BookshelfNG multi-database merge is already using this data directory.", exception);
            }
        }

        private static void RestrictDirectory(string path)
        {
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }

        private static void RestrictFile(string path)
        {
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        }

        private static void EnsureConversionSpace(string appDataFolder, string readarrPath, string audioarrPath)
        {
            long sourceBytes = 0;
            foreach (var databasePath in new[] { readarrPath, audioarrPath })
            {
                foreach (var suffix in new[] { string.Empty, "-wal", "-journal" })
                {
                    var path = databasePath + suffix;
                    if (File.Exists(path))
                    {
                        sourceBytes = checked(sourceBytes + new FileInfo(path).Length);
                    }
                }
            }

            var requiredBytes = checked(sourceBytes * 5 + 128L * 1024 * 1024);
            var fullPath = Path.GetFullPath(appDataFolder);
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            var mount = DriveInfo.GetDrives()
                .Where(drive => drive.IsReady && IsPathOnDrive(fullPath, drive.RootDirectory.FullName, comparison))
                .MaxBy(drive => drive.RootDirectory.FullName.Length);
            var availableBytes = mount?.AvailableFreeSpace ?? 0;
            if (mount == null || availableBytes < requiredBytes)
            {
                throw new IOException(
                    $"The two BookshelfNG databases need approximately {requiredBytes / (1024 * 1024)} MiB of free space for staged merge and conversion; only {availableBytes / (1024 * 1024)} MiB is available. No source database was changed.");
            }
        }

        private static bool IsPathOnDrive(string path, string driveRoot, StringComparison comparison)
        {
            var root = Path.GetFullPath(driveRoot);
            return path.StartsWith(root, comparison);
        }

        private static SqliteConnection Open(string path, SqliteOpenMode mode)
        {
            var connection = new SqliteConnection(ConnectionString(path, mode));
            connection.Open();
            return connection;
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

        private static string Quote(string identifier)
        {
            return "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
        }

        private static bool PathEquals(string left, string right)
        {
            return string.Equals(
                Path.GetFullPath(left),
                Path.GetFullPath(right),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }

        private sealed record SqliteColumn(string Name, string Type, int PrimaryKeyOrder);
    }

    internal sealed class BookshelfMergeReceipt
    {
        private const string VersionLine = "bookshelfng-merge-v1";
        private readonly string _readarrFingerprint;
        private readonly string _audioarrFingerprint;
        private readonly string _targetFingerprint;
        private readonly string _state;

        public BookshelfMergeReceipt(string readarrFingerprint, string audioarrFingerprint, string targetFingerprint, string state)
        {
            _readarrFingerprint = readarrFingerprint;
            _audioarrFingerprint = audioarrFingerprint;
            _targetFingerprint = targetFingerprint;
            _state = state;
        }

        public BookshelfMergeReceipt WithState(string state)
        {
            return new BookshelfMergeReceipt(_readarrFingerprint, _audioarrFingerprint, _targetFingerprint, state);
        }

        public void Write(string path)
        {
            EnsureRegularReceipt(path);
            if (_state is not ("prepared" or "complete" or "rolled-back"))
            {
                throw new InvalidOperationException("A BookshelfNG merge receipt has an unsupported state.");
            }

            var content = string.Join("\n", new[]
            {
                VersionLine,
                "state=" + _state,
                "readarr=" + _readarrFingerprint,
                "audioarr=" + _audioarrFingerprint,
                "target=" + _targetFingerprint,
                string.Empty
            });
            var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                File.WriteAllText(temporary, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                if (!OperatingSystem.IsWindows())
                {
                    File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                }

                File.Move(temporary, path, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporary))
                {
                    File.Delete(temporary);
                }
            }
        }

        public static IReadOnlySet<string> ValidateForNativeDatabase(string nativePath)
        {
            var receiptPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(nativePath)), "bookshelfng-merge.receipt");
            EnsureRegularReceipt(receiptPath);
            if (!File.Exists(receiptPath)) return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            string[] lines;
            try
            {
                lines = File.ReadAllLines(receiptPath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                throw new InvalidDataException("The BookshelfNG merge receipt could not be read. Preserve the migration files and resolve it before startup.", exception);
            }

            if (lines.Length != 5 || lines[0] != VersionLine ||
                (lines[1] != "state=prepared" && lines[1] != "state=complete" && lines[1] != "state=rolled-back") ||
                !lines[2].StartsWith("readarr=sha256:", StringComparison.Ordinal) ||
                !lines[3].StartsWith("audioarr=sha256:", StringComparison.Ordinal) ||
                !lines[4].StartsWith("target=sha256:", StringComparison.Ordinal))
            {
                throw new InvalidDataException("The BookshelfNG merge receipt is invalid. Preserve the database files and resolve the merge state before startup.");
            }

            var readarrPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(nativePath)), "readarr.db");
            var audioarrPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(nativePath)), "audioarr.db");
            var expectedReadarr = lines[2]["readarr=".Length..];
            var expectedAudioarr = lines[3]["audioarr=".Length..];
            var expectedTarget = lines[4]["target=".Length..];
            if (lines[1] == "state=prepared" &&
                !string.Equals(BookshelfMigrationJournal.ComputeFingerprint(nativePath), expectedTarget, StringComparison.Ordinal))
            {
                throw new InvalidDataException("The native database does not match the staged target recorded by the prepared BookshelfNG merge. Preserve all migration files and resolve the state before startup.");
            }

            ValidateSourceRetained(readarrPath, expectedReadarr);
            ValidateSourceRetained(audioarrPath, expectedAudioarr);
            if (lines[1] == "state=prepared")
            {
                new BookshelfMergeReceipt(expectedReadarr, expectedAudioarr, expectedTarget, "complete").Write(receiptPath);
            }

            return new HashSet<string>(new[] { "readarr.db", "audioarr.db" }, StringComparer.OrdinalIgnoreCase);
        }

        public static void MarkRolledBack(string appDataFolder)
        {
            var path = Path.Combine(Path.GetFullPath(appDataFolder), "bookshelfng-merge.receipt");
            EnsureRegularReceipt(path);
            if (!File.Exists(path)) return;

            var lines = File.ReadAllLines(path);
            if (lines.Length != 5 || lines[0] != VersionLine ||
                !lines[2].StartsWith("readarr=sha256:", StringComparison.Ordinal) ||
                !lines[3].StartsWith("audioarr=sha256:", StringComparison.Ordinal) ||
                !lines[4].StartsWith("target=sha256:", StringComparison.Ordinal))
            {
                throw new InvalidDataException("The BookshelfNG merge receipt could not be marked rolled back because its format is invalid.");
            }

            new BookshelfMergeReceipt(
                lines[2]["readarr=".Length..],
                lines[3]["audioarr=".Length..],
                lines[4]["target=".Length..],
                "rolled-back").Write(path);
        }

        public static string FingerprintDatabaseFamily(string databasePath)
        {
            var manifest = new StringBuilder();
            foreach (var suffix in new[] { string.Empty, "-wal", "-journal" })
            {
                var path = databasePath + suffix;
                manifest.Append(Path.GetFileName(path));
                manifest.Append('\0');
                manifest.Append(BookshelfMigrationJournal.ComputeFingerprint(path));
                manifest.Append('\n');
            }

            return "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(manifest.ToString()))).ToLowerInvariant();
        }

        private static void ValidateSourceRetained(string path, string expectedFingerprint)
        {
            EnsureRegularReceipt(path);
            foreach (var suffix in new[] { "-wal", "-journal", "-shm" })
            {
                EnsureRegularReceipt(path + suffix);
            }

            if (!File.Exists(path) && !DatabaseLineageGuard.HasNonEmptyDurableSidecar(path))
            {
                // Operators may remove retained legacy databases after deciding
                // they no longer need a source rollback copy.
                return;
            }

            if (!string.Equals(FingerprintDatabaseFamily(path), expectedFingerprint, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"Retained BookshelfNG source '{Path.GetFileName(path)}' changed after the merge. Preserve the current files and reconcile the source before startup.");
            }
        }

        private static void EnsureRegularReceipt(string path)
        {
            var file = new FileInfo(path);
            var directory = new DirectoryInfo(path);
            if (file.LinkTarget is not null || directory.LinkTarget is not null || Directory.Exists(path) ||
                (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0))
            {
                throw new InvalidDataException($"BookshelfNG migration receipt/source path '{Path.GetFileName(path)}' is not a regular file.");
            }
        }
    }
}
