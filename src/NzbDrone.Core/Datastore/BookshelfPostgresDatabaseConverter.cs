using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Globalization;
using System.IO;
using System.Linq;
using Npgsql;

namespace NzbDrone.Core.Datastore
{
    /// <summary>
    /// Converts a BookshelfNG PostgreSQL schema into ChaptarrNG in the same database.
    /// Older recognized source schemas are upgraded on a disposable snapshot; the
    /// original tables move to a retained backup schema only after validation.
    /// </summary>
    public sealed class BookshelfPostgresDatabaseConverter
    {
        private const int SupportedSourceMigration = 158;

        public BookshelfDatabaseConversionResult Convert(
            string sourceConnectionString,
            Action<string> migrateNativeTarget,
            string mediaMapBasePath,
            bool previewOnly = false,
            Action<string> migrateBookshelfSource = null,
            Action<string, string> reportProgress = null,
            Action<string, string> reportOutcome = null,
            string sourceApiBasePath = null)
        {
            if (migrateNativeTarget == null)
            {
                throw new ArgumentNullException(nameof(migrateNativeTarget));
            }

            if (string.IsNullOrWhiteSpace(mediaMapBasePath))
            {
                throw new ArgumentException("A writable app-data path is required for migration review files.", nameof(mediaMapBasePath));
            }

            var connectionBuilder = new NpgsqlConnectionStringBuilder(sourceConnectionString)
            {
                Pooling = false,
                Enlist = false
            };

            using var sourceConnection = new NpgsqlConnection(connectionBuilder.ConnectionString);
            sourceConnection.Open();
            var sourceSchema = GetCurrentSchema(sourceConnection);
            AcquireMigrationLock(sourceConnection);

            var snapshotSchema = NewSchemaName("bookshelf_snapshot");
            var stageSchema = NewSchemaName("chaptarr_stage");
            var backupSchema = NewBackupSchemaName();
            var promoted = false;

            try
            {
                if (DatabaseLineageGuard.Detect(sourceConnection, DatabaseType.PostgreSQL) != DatabaseLineage.BookshelfNG)
                {
                    throw new InvalidDataException("The database does not match the supported BookshelfNG PostgreSQL schema.");
                }

                reportProgress?.Invoke("detected", sourceSchema);
                var sourceVersion = BookshelfDatabaseImporter.ValidateSourceBackup(sourceConnection);
                reportProgress?.Invoke("preflighted", sourceSchema);
                if (!previewOnly)
                {
                    var cleanedSchemas = CleanOrphanedTemporarySchemas(sourceConnection, sourceSchema);
                    reportOutcome?.Invoke("orphan-stages-cleaned", cleanedSchemas.ToString(CultureInfo.InvariantCulture));
                }
                var qualityProfileIdentityHighWater = CreateConsistentSnapshot(sourceConnection, sourceSchema, snapshotSchema);
                reportProgress?.Invoke("snapshotted", snapshotSchema);

                if (sourceVersion < SupportedSourceMigration)
                {
                    if (migrateBookshelfSource == null)
                    {
                        throw new InvalidDataException(
                            $"BookshelfNG PostgreSQL schema version {sourceVersion} requires the bundled source migration chain. The source database was not changed.");
                    }

                    migrateBookshelfSource(BuildSchemaConnectionString(connectionBuilder, snapshotSchema));
                }

                using (var snapshot = OpenSchemaConnection(connectionBuilder, snapshotSchema))
                {
                    BookshelfDatabaseImporter.ValidateSourceSchema(snapshot);
                }

                using (var stageCreation = sourceConnection.BeginTransaction())
                {
                    CreateSchema(sourceConnection, stageSchema, stageCreation);
                    MarkTemporarySchema(sourceConnection, stageSchema, sourceSchema, stageCreation);
                    stageCreation.Commit();
                }

                var stagedConnectionString = BuildSchemaConnectionString(connectionBuilder, stageSchema);
                migrateNativeTarget(stagedConnectionString);

                BookshelfDatabaseImportReport importReport;
                using (var snapshot = OpenSchemaConnection(connectionBuilder, snapshotSchema))
                using (var target = OpenSchemaConnection(connectionBuilder, stageSchema))
                {
                    BookshelfDatabaseImporter.ValidateSourceSchema(snapshot);
                    var sourceTables = GetSourceTableCounts(snapshot);
                    using var transaction = target.BeginTransaction();
                    importReport = BookshelfDatabaseImporter.Import(
                        snapshot,
                        target,
                        transaction,
                        mediaMapBasePath,
                        sourceVersion,
                        sourceApiBasePath,
                        qualityProfileIdentityHighWater);
                    ValidateImportReport(sourceTables, importReport);
                    transaction.Commit();
                }

                if (DatabaseLineageGuard.Detect(stagedConnectionString, DatabaseType.PostgreSQL) != DatabaseLineage.Chaptarr)
                {
                    throw new InvalidDataException("The staged conversion target did not migrate to a native Chaptarr schema.");
                }
                reportProgress?.Invoke("converted", stageSchema);

                if (previewOnly)
                {
                    return new BookshelfDatabaseConversionResult(null, importReport, previewOnly: true);
                }

                importReport.ThrowIfBlockingIssues();

                DropSchemaIfExists(sourceConnection, snapshotSchema);
                snapshotSchema = null;
                Promote(
                    sourceConnection,
                    sourceSchema,
                    stageSchema,
                    backupSchema,
                    DatabaseLineage.Chaptarr,
                    DatabaseLineage.BookshelfNG);
                promoted = true;
                try
                {
                    reportProgress?.Invoke("promoted", backupSchema);
                }
                catch
                {
                    // Promotion is complete. The subsequent package health check can
                    // reconcile the journal if recording this final checkpoint fails.
                }
                return new BookshelfDatabaseConversionResult(backupSchema, importReport, previewOnly: false);
            }
            finally
            {
                if (snapshotSchema != null)
                {
                    DropSchemaIfExists(sourceConnection, snapshotSchema);
                }
                if (!promoted)
                {
                    DropSchemaIfExists(sourceConnection, stageSchema);
                }

                ReleaseMigrationLock(sourceConnection);
            }
        }

        public BookshelfDatabaseRollbackResult Restore(
            string currentConnectionString,
            string bookshelfBackupSchema,
            bool previewOnly = false)
        {
            if (!string.IsNullOrWhiteSpace(bookshelfBackupSchema) &&
                (!bookshelfBackupSchema.StartsWith("bookshelfng_backup_", StringComparison.Ordinal) ||
                 !bookshelfBackupSchema.All(character => char.IsAsciiLetterOrDigit(character) || character == '_')))
            {
                throw new InvalidDataException("A retained BookshelfNG PostgreSQL backup schema name is required. No database was changed.");
            }

            var connectionBuilder = new NpgsqlConnectionStringBuilder(currentConnectionString)
            {
                Pooling = false,
                Enlist = false
            };

            using var connection = new NpgsqlConnection(connectionBuilder.ConnectionString);
            connection.Open();
            var currentSchema = GetCurrentSchema(connection);
            AcquireMigrationLock(connection);

            try
            {
                if (DatabaseLineageGuard.Detect(connection, DatabaseType.PostgreSQL) != DatabaseLineage.Chaptarr)
                {
                    throw new InvalidDataException("The current PostgreSQL schema is not a ChaptarrNG database. No database was changed.");
                }

                bookshelfBackupSchema ??= FindLatestBookshelfBackupSchema(connectionBuilder);
                if (!bookshelfBackupSchema.StartsWith("bookshelfng_backup_", StringComparison.Ordinal) ||
                    !bookshelfBackupSchema.All(character => char.IsAsciiLetterOrDigit(character) || character == '_'))
                {
                    throw new InvalidDataException("The newest retained PostgreSQL backup schema has an invalid name. No database was changed.");
                }

                using (var bookshelfBackup = OpenSchemaConnection(connectionBuilder, bookshelfBackupSchema))
                {
                    BookshelfDatabaseImporter.ValidateSourceBackup(bookshelfBackup);
                }

                EnsureSupportedSchemaObjects(connection, currentSchema);
                EnsureSupportedSchemaObjects(connection, bookshelfBackupSchema);

                if (previewOnly)
                {
                    return new BookshelfDatabaseRollbackResult(bookshelfBackupSchema, null, previewOnly: true);
                }

                var chaptarrBackupSchema = NewBackupSchemaName("chaptarrng_backup");
                Promote(
                    connection,
                    currentSchema,
                    bookshelfBackupSchema,
                    chaptarrBackupSchema,
                    DatabaseLineage.BookshelfNG,
                    DatabaseLineage.Chaptarr);
                return new BookshelfDatabaseRollbackResult(bookshelfBackupSchema, chaptarrBackupSchema, previewOnly: false);
            }
            finally
            {
                ReleaseMigrationLock(connection);
            }
        }

        private static int CreateConsistentSnapshot(NpgsqlConnection connection, string sourceSchema, string snapshotSchema)
        {
            var tables = GetSchemaTables(connection, sourceSchema);
            if (tables.Count == 0)
            {
                throw new InvalidDataException("BookshelfNG PostgreSQL source schema has no tables. No database was changed.");
            }

            EnsureSupportedSchemaObjects(connection, sourceSchema);

            using var transaction = connection.BeginTransaction(IsolationLevel.RepeatableRead);
            // CREATE TABLE AS retains rows but not the source allocator state.
            var qualityProfileIdentityHighWater = BookshelfDatabaseImporter.ReadQualityProfileIdentityHighWater(connection, transaction);
            CreateSchema(connection, snapshotSchema, transaction);
            MarkTemporarySchema(connection, snapshotSchema, sourceSchema, transaction);
            foreach (var table in tables)
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = $"CREATE TABLE {Quote(snapshotSchema)}.{Quote(table)} AS SELECT * FROM {Quote(sourceSchema)}.{Quote(table)};"; // nosemgrep: csharp.lang.security.sqli.csharp-sqli.csharp-sqli -- all interpolated values are escaped SQL identifiers.
                command.ExecuteNonQuery();
            }

            transaction.Commit();
            return qualityProfileIdentityHighWater;
        }

        private static void MarkTemporarySchema(NpgsqlConnection connection, string temporarySchema, string sourceSchema, DbTransaction transaction = null)
        {
            var marker = "chaptarrng-bookshelfng-converter:" + System.Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(sourceSchema));
            Execute(connection, transaction, $"COMMENT ON SCHEMA {Quote(temporarySchema)} IS '{marker}';"); // nosemgrep: csharp.lang.security.sqli.csharp-sqli.csharp-sqli -- schema is identifier-quoted and marker is fixed text plus Base64.
        }

        private static int CleanOrphanedTemporarySchemas(NpgsqlConnection connection, string sourceSchema)
        {
            var marker = "chaptarrng-bookshelfng-converter:" + System.Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(sourceSchema));
            var schemas = new List<string>();
            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"
                    SELECT n.nspname
                    FROM pg_namespace n
                    WHERE (n.nspname LIKE 'bookshelf_snapshot_%' OR n.nspname LIKE 'chaptarr_stage_%')
                      AND obj_description(n.oid, 'pg_namespace') = @marker
                    ORDER BY n.nspname;";
                AddParameter(command, "@marker", marker);
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    schemas.Add(reader.GetString(0));
                }
            }

            foreach (var schema in schemas)
            {
                DropSchemaIfExists(connection, schema);
            }

            return schemas.Count;
        }

        private static void Promote(
            NpgsqlConnection connection,
            string sourceSchema,
            string targetSchema,
            string backupSchema,
            DatabaseLineage promotedLineage,
            DatabaseLineage backupLineage)
        {
            EnsureSupportedSchemaObjects(connection, sourceSchema);
            EnsureSupportedSchemaObjects(connection, targetSchema);

            var sourceTables = GetSchemaTables(connection, sourceSchema);
            var targetTables = GetSchemaTables(connection, targetSchema);
            if (sourceTables.Count == 0 || targetTables.Count == 0)
            {
                throw new InvalidDataException("The source or staged PostgreSQL schema is empty. No database was changed.");
            }

            using var transaction = connection.BeginTransaction();
            CreateSchema(connection, backupSchema, transaction);

            foreach (var table in sourceTables)
            {
                Execute(connection, transaction, $"ALTER TABLE {Quote(sourceSchema)}.{Quote(table)} SET SCHEMA {Quote(backupSchema)};");
            }

            MoveRemainingSequences(connection, transaction, sourceSchema, backupSchema);

            foreach (var table in targetTables)
            {
                Execute(connection, transaction, $"ALTER TABLE {Quote(targetSchema)}.{Quote(table)} SET SCHEMA {Quote(sourceSchema)};");
            }

            MoveRemainingSequences(connection, transaction, targetSchema, sourceSchema);

            Execute(connection, transaction, $"SET LOCAL search_path TO {Quote(sourceSchema)};");
            if (DatabaseLineageGuard.Detect(connection, DatabaseType.PostgreSQL, transaction) != promotedLineage)
            {
                throw new InvalidDataException($"The promoted PostgreSQL schema failed {promotedLineage} lineage validation; the operation was rolled back.");
            }

            Execute(connection, transaction, $"SET LOCAL search_path TO {Quote(backupSchema)};");
            if (DatabaseLineageGuard.Detect(connection, DatabaseType.PostgreSQL, transaction) != backupLineage)
            {
                throw new InvalidDataException($"The retained PostgreSQL backup failed {backupLineage} lineage validation; the operation was rolled back.");
            }

            if (!string.Equals(targetSchema, backupSchema, StringComparison.Ordinal))
            {
                // Table-owned indexes, constraints, row types, and sequences move with
                // their tables. Refuse promotion if the staged schema still owns any
                // independent object instead of deleting it through CASCADE.
                Execute(connection, transaction, $"DROP SCHEMA {Quote(targetSchema)};");
            }

            transaction.Commit();
        }

        private static void EnsureSupportedSchemaObjects(NpgsqlConnection connection, string schema)
        {
            using var command = connection.CreateCommand();
            command.CommandText = @"
                SELECT c.relname, c.relkind
                  FROM pg_class c
                  JOIN pg_namespace n ON n.oid = c.relnamespace
                 WHERE n.nspname = @schema
                   AND c.relkind IN ('v', 'm', 'f', 'p')
                 LIMIT 1;";
            AddParameter(command, "@schema", schema);
            using (var reader = command.ExecuteReader())
            {
                if (reader.Read())
                {
                    throw new InvalidDataException(
                        $"PostgreSQL schema '{schema}' contains a view, materialized view, foreign table, or partitioned table ('{reader.GetString(0)}') that cannot be moved safely as part of this migration. No database was changed.");
                }
            }

            using var otherObjects = connection.CreateCommand();
            otherObjects.CommandText = @"
                SELECT routine.routine_name, 'routine'
                  FROM information_schema.routines routine
                 WHERE routine.routine_schema = @schema
                UNION ALL
                SELECT customType.typname, 'type'
                  FROM pg_type customType
                  JOIN pg_namespace n ON n.oid = customType.typnamespace
                 WHERE n.nspname = @schema
                   AND customType.typrelid = 0
                   AND customType.typtype IN ('d', 'e', 'r', 'm')
                 LIMIT 1;";
            AddParameter(otherObjects, "@schema", schema);
            using var otherReader = otherObjects.ExecuteReader();
            if (otherReader.Read())
            {
                throw new InvalidDataException(
                    $"PostgreSQL schema '{schema}' contains a standalone {otherReader.GetString(1)} ('{otherReader.GetString(0)}') that cannot be moved safely as part of this migration. No database was changed.");
            }
        }

        private static HashSet<string> GetSchemaTables(NpgsqlConnection connection, string schema, DbTransaction transaction = null)
        {
            var result = new HashSet<string>(StringComparer.Ordinal);
            using var command = connection.CreateCommand();
            SetTransaction(command, transaction);
            command.CommandText = @"
                SELECT table_name
                  FROM information_schema.tables
                 WHERE table_schema = @schema
                   AND table_type = 'BASE TABLE'
                 ORDER BY table_name;";
            AddParameter(command, "@schema", schema);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                result.Add(reader.GetString(0));
            }

            return result;
        }

        private static Dictionary<string, long> GetSourceTableCounts(DbConnection source)
        {
            var counts = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (var table in BookshelfDatabaseImporter.GetTables(source).OrderBy(name => name, StringComparer.Ordinal))
            {
                using var command = source.CreateCommand();
                command.CommandText = $"SELECT COUNT(*) FROM {Quote(table)};";
                counts.Add(table, System.Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture));
            }

            return counts;
        }

        private static void ValidateImportReport(IReadOnlyDictionary<string, long> sourceTables, BookshelfDatabaseImportReport report)
        {
            if (report == null)
            {
                throw new InvalidDataException("BookshelfNG import did not return row accounting; the staged PostgreSQL schema was not promoted.");
            }

            var missing = sourceTables.Keys.Except(report.Tables.Keys, StringComparer.Ordinal).OrderBy(name => name, StringComparer.Ordinal).ToArray();
            var unknown = report.Tables.Keys.Except(sourceTables.Keys, StringComparer.Ordinal).OrderBy(name => name, StringComparer.Ordinal).ToArray();
            if (missing.Length > 0 || unknown.Length > 0)
            {
                throw new InvalidDataException(
                    "BookshelfNG PostgreSQL row accounting is incomplete (unreported source tables: " + string.Join(", ", missing) +
                    "; reported tables absent from source: " + string.Join(", ", unknown) + "). The staged schema was not promoted.");
            }

            foreach (var sourceTable in sourceTables)
            {
                var disposition = report.Tables[sourceTable.Key];
                if (disposition.SourceRows != sourceTable.Value || disposition.AccountedRows != sourceTable.Value)
                {
                    throw new InvalidDataException(
                        $"BookshelfNG PostgreSQL row accounting mismatch for '{sourceTable.Key}' " +
                        $"({disposition.AccountedRows}/{sourceTable.Value} rows classified); the staged schema was not promoted.");
                }
            }
        }

        private static void MoveRemainingSequences(NpgsqlConnection connection, DbTransaction transaction, string fromSchema, string toSchema)
        {
            using var command = connection.CreateCommand();
            SetTransaction(command, transaction);
            command.CommandText = "SELECT sequence_name FROM information_schema.sequences WHERE sequence_schema = @schema ORDER BY sequence_name;";
            AddParameter(command, "@schema", fromSchema);
            var sequences = new List<string>();
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read()) sequences.Add(reader.GetString(0));
            }

            foreach (var sequence in sequences)
            {
                Execute(connection, transaction, $"ALTER SEQUENCE {Quote(fromSchema)}.{Quote(sequence)} SET SCHEMA {Quote(toSchema)};");
            }
        }

        private static string GetCurrentSchema(NpgsqlConnection connection)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT current_schema();";
            var schema = command.ExecuteScalar()?.ToString();
            if (string.IsNullOrWhiteSpace(schema))
            {
                throw new InvalidDataException("PostgreSQL has no current schema for the BookshelfNG database connection.");
            }

            return schema;
        }

        private static string FindLatestBookshelfBackupSchema(NpgsqlConnectionStringBuilder connectionBuilder)
        {
            string latest;
            using (var connection = new NpgsqlConnection(connectionBuilder.ConnectionString))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = @"
                    SELECT schema_name
                      FROM information_schema.schemata
                     WHERE left(schema_name, length(@prefix)) = @prefix
                     ORDER BY schema_name DESC
                     LIMIT 1;";
                AddParameter(command, "@prefix", "bookshelfng_backup_");
                latest = command.ExecuteScalar()?.ToString();
            }

            if (string.IsNullOrWhiteSpace(latest))
            {
                throw new InvalidDataException("No retained BookshelfNG PostgreSQL backup schema was found. No database was changed.");
            }

            return latest;
        }

        private static void CreateSchema(NpgsqlConnection connection, string schema, DbTransaction transaction = null)
        {
            Execute(connection, transaction, $"CREATE SCHEMA {Quote(schema)};");
        }

        private static void DropSchemaIfExists(NpgsqlConnection connection, string schema)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT 1 FROM information_schema.schemata WHERE schema_name = @schema;";
            AddParameter(command, "@schema", schema);
            if (command.ExecuteScalar() == null)
            {
                return;
            }

            Execute(connection, null, $"DROP SCHEMA {Quote(schema)} CASCADE;");
        }

        private static void Execute(NpgsqlConnection connection, DbTransaction transaction, string sql)
        {
            using var command = connection.CreateCommand();
            SetTransaction(command, transaction);
            command.CommandText = sql; // nosemgrep: csharp.lang.security.sqli.csharp-sqli.csharp-sqli -- callers use fixed SQL plus identifiers escaped by Quote; data values are parameters.
            command.ExecuteNonQuery();
        }

        private static void SetTransaction(NpgsqlCommand command, DbTransaction transaction)
        {
            if (transaction == null) return;
            if (transaction is not NpgsqlTransaction postgresTransaction)
            {
                throw new ArgumentException("The PostgreSQL BookshelfNG converter requires an Npgsql transaction.", nameof(transaction));
            }

            command.Transaction = postgresTransaction;
        }

        private static void AcquireMigrationLock(NpgsqlConnection connection)
        {
            Execute(connection, null, "SELECT pg_advisory_lock(hashtext(current_database()), hashtext(current_schema()));");
        }

        private static void ReleaseMigrationLock(NpgsqlConnection connection)
        {
            if (connection.State != ConnectionState.Open) return;
            Execute(connection, null, "SELECT pg_advisory_unlock(hashtext(current_database()), hashtext(current_schema()));");
        }

        private static string BuildSchemaConnectionString(NpgsqlConnectionStringBuilder source, string schema)
        {
            var builder = new NpgsqlConnectionStringBuilder(source.ConnectionString)
            {
                SearchPath = schema,
                Pooling = false,
                Enlist = false
            };
            return builder.ConnectionString;
        }

        private static NpgsqlConnection OpenSchemaConnection(NpgsqlConnectionStringBuilder source, string schema)
        {
            var connection = new NpgsqlConnection(BuildSchemaConnectionString(source, schema));
            connection.Open();
            return connection;
        }

        private static void AddParameter(DbCommand command, string name, object value)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value ?? DBNull.Value;
            command.Parameters.Add(parameter);
        }

        private static string NewSchemaName(string prefix)
        {
            return prefix + "_" + Guid.NewGuid().ToString("N").Substring(0, 12);
        }

        private static string NewBackupSchemaName(string prefix = "bookshelfng_backup")
        {
            return prefix + "_" + DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) + "_" +
                   Guid.NewGuid().ToString("N").Substring(0, 8);
        }

        private static string Quote(string identifier)
        {
            return "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
        }
    }
}
