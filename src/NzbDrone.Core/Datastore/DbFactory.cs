using System;
using System.Data.Common;
using Microsoft.Data.Sqlite;
using System.Net.Sockets;
using System.Threading;
using NLog;
using Npgsql;
using NzbDrone.Common.Disk;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Common.Exceptions;
using NzbDrone.Common.Instrumentation;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace NzbDrone.Core.Datastore
{
    public interface IDbFactory
    {
        IDatabase Create(MigrationType migrationType = MigrationType.Main);
        IDatabase Create(MigrationContext migrationContext);
    }

    public class DbFactory : IDbFactory
    {
        private static readonly Logger Logger = NzbDroneLogger.GetLogger(typeof(DbFactory));

        private const int SqliteErrorCodePerm = 3;
        private const int SqliteErrorCodeBusy = 5;
        private const int SqliteErrorCodeLocked = 6;
        private const int SqliteErrorCodeReadOnly = 8;
        private const int SqliteErrorCodeIoErr = 10;
        private const int SqliteErrorCodeCorrupt = 11;
        private const int SqliteErrorCodeFull = 13;
        private const int SqliteErrorCodeCantOpen = 14;
        private const int SqliteErrorCodeAuth = 23;
        private const int SqliteErrorCodeNotADatabase = 26;

        private readonly IMigrationController _migrationController;
        private readonly IConnectionStringFactory _connectionStringFactory;
        private readonly IDiskProvider _diskProvider;
        private readonly IRestoreDatabase _restoreDatabaseService;

        static DbFactory()
        {
            InitializeEnvironment();

            // Ensure Dapper stores/loads DateTime as UTC consistently
            Dapper.SqlMapper.AddTypeHandler(typeof(DateTime), new SqliteUtcDateTimeHandler());
            Dapper.SqlMapper.AddTypeHandler(typeof(DateTime?), new SqliteNullableUtcDateTimeHandler());

            TableMapping.Map();
        }

        private static void InitializeEnvironment()
        {
            // No specific environment needed for Microsoft.Data.Sqlite; native is bundled via SQLitePCLRaw
        }

        public DbFactory(IMigrationController migrationController,
                         IConnectionStringFactory connectionStringFactory,
                         IDiskProvider diskProvider,
                         IRestoreDatabase restoreDatabaseService)
        {
            _migrationController = migrationController;
            _connectionStringFactory = connectionStringFactory;
            _diskProvider = diskProvider;
            _restoreDatabaseService = restoreDatabaseService;
        }

        public IDatabase Create(MigrationType migrationType = MigrationType.Main)
        {
            return Create(new MigrationContext(migrationType));
        }

        public IDatabase Create(MigrationContext migrationContext)
        {
            DatabaseConnectionInfo connectionInfo;

            switch (migrationContext.MigrationType)
            {
                case MigrationType.Main:
                    {
                        connectionInfo = _connectionStringFactory.MainDbConnection;
                        CreateMain(connectionInfo.ConnectionString, migrationContext, connectionInfo.DatabaseType);

                        break;
                    }

                case MigrationType.Log:
                    {
                        connectionInfo = _connectionStringFactory.LogDbConnection;
                        CreateLog(connectionInfo.ConnectionString, migrationContext, connectionInfo.DatabaseType);

                        break;
                    }

                case MigrationType.Cache:
                    {
                        connectionInfo = _connectionStringFactory.CacheDbConnection;
                        CreateLog(connectionInfo.ConnectionString, migrationContext, connectionInfo.DatabaseType);

                        break;
                    }

                default:
                    {
                        throw new ArgumentException("Invalid MigrationType");
                    }
            }

            var db = new Database(migrationContext.MigrationType.ToString(), () =>
            {
                DbConnection conn;

                if (connectionInfo.DatabaseType == DatabaseType.SQLite)
                {
                    conn = new SqliteConnection(connectionInfo.ConnectionString);
                }
                else
                {
                    conn = new NpgsqlConnection(connectionInfo.ConnectionString);
                }

                conn.Open();

                // For SQLite connections, ensure sane PRAGMAs for write performance and concurrency
                if (conn is SqliteConnection sqlite)
                {
                    using (var cmd = sqlite.CreateCommand())
                    {
                        cmd.CommandText = @"PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA busy_timeout=5000;";
                        try { cmd.ExecuteNonQuery(); } catch { /* best effort */ }
                    }
                }
                return conn;
            });

            return db;
        }

        private void CreateMain(string connectionString, MigrationContext migrationContext, DatabaseType databaseType)
        {
            try
            {
                var restoreInProgress = false;

                try
                {
                    if (databaseType == DatabaseType.SQLite)
                    {
                        restoreInProgress = _restoreDatabaseService.Restore();
                    }

                    _migrationController.Migrate(connectionString, migrationContext, databaseType);

                    if (restoreInProgress)
                    {
                        // Migration succeeded. From this point forward the candidate is the live
                        // database; cleanup failure must not roll it back.
                        restoreInProgress = false;
                        _restoreDatabaseService.Commit();
                    }
                }
                catch
                {
                    if (restoreInProgress)
                    {
                        _restoreDatabaseService.Rollback();
                    }

                    throw;
                }
            }
            catch (SqliteException e)
            {
                var fileName = _connectionStringFactory.GetDatabasePath(connectionString);

                var sqliteErrorCode = GetPrimarySqliteErrorCode(e);

                if (IsCorruptSqliteError(sqliteErrorCode))
                {
                    throw new CorruptDatabaseException("Database file: {0} is corrupt, restore from backup if available. See: https://discord.gg/nqFGsGUug2", e, fileName);
                }

                throw new ChaptarrStartupException(e, GetNonCorruptSqliteErrorMessage(sqliteErrorCode, fileName));
            }
            catch (NpgsqlException e)
            {
                if (e.InnerException is SocketException)
                {
                    var retryCount = 3;

                    while (true)
                    {
                        Logger.Error(e, "Failure to connect to Postgres DB, {0} retries remaining", retryCount);

                        Thread.Sleep(5000);

                        try
                        {
                            _migrationController.Migrate(connectionString, migrationContext, databaseType);
                            return;
                        }
                        catch (Exception ex)
                        {
                            if (--retryCount > 0)
                            {
                                continue;
                            }

                            throw new ChaptarrStartupException(ex, "Error creating main database");
                        }
                    }
                }
                else
                {
                    throw new ChaptarrStartupException(e, "Error creating main database");
                }
            }
            catch (Exception e)
            {
                throw new ChaptarrStartupException(e, "Error creating main database");
            }
        }

        private void CreateLog(string connectionString, MigrationContext migrationContext, DatabaseType databaseType)
        {
            try
            {
                _migrationController.Migrate(connectionString, migrationContext, databaseType);
            }
            catch (SqliteException e)
            {
                var fileName = _connectionStringFactory.GetDatabasePath(connectionString);

                var sqliteErrorCode = GetPrimarySqliteErrorCode(e);

                if (!IsCorruptSqliteError(sqliteErrorCode))
                {
                    throw new ChaptarrStartupException(e, GetNonCorruptSqliteErrorMessage(sqliteErrorCode, fileName));
                }

                Logger.Error(e, "Logging database is corrupt, attempting to recreate it automatically");

                try
                {
                    _diskProvider.DeleteFile(fileName + "-shm");
                    _diskProvider.DeleteFile(fileName + "-wal");
                    _diskProvider.DeleteFile(fileName + "-journal");
                    _diskProvider.DeleteFile(fileName);
                }
                catch (Exception)
                {
                    Logger.Error("Unable to recreate logging database automatically. It will need to be removed manually.");
                }

                _migrationController.Migrate(connectionString, migrationContext, databaseType);
            }
            catch (Exception e)
            {
                throw new ChaptarrStartupException(e, "Error creating log database");
            }
        }

        private static int GetPrimarySqliteErrorCode(SqliteException exception)
        {
            // Be defensive: some SQLite error codes pack the primary result in the low 8 bits.
            return exception.SqliteErrorCode & 0xFF;
        }

        private static bool IsCorruptSqliteError(int sqliteErrorCode)
        {
            return sqliteErrorCode == SqliteErrorCodeCorrupt || sqliteErrorCode == SqliteErrorCodeNotADatabase;
        }

        private static string GetNonCorruptSqliteErrorMessage(int sqliteErrorCode, string databasePath)
        {
            return sqliteErrorCode switch
            {
                SqliteErrorCodeCantOpen => $"Unable to open database file: {databasePath}. This usually means the folder does not exist or is not writable.",
                SqliteErrorCodeReadOnly => $"Database file is read-only: {databasePath}. Ensure the file and folder are writable.",
                SqliteErrorCodePerm => $"Permission denied opening database file: {databasePath}. Ensure the file and folder are writable.",
                SqliteErrorCodeBusy => $"Database file is busy: {databasePath}. Ensure only one instance is running and try again.",
                SqliteErrorCodeLocked => $"Database file is locked: {databasePath}. Ensure only one instance is running and try again.",
                SqliteErrorCodeFull => $"Disk is full while writing database file: {databasePath}. Free up space and try again.",
                SqliteErrorCodeIoErr => $"I/O error while accessing database file: {databasePath}. Check disk health and permissions.",
                SqliteErrorCodeAuth => $"Not authorized to access database file: {databasePath}. Check file and folder permissions.",
                _ => $"Database error while opening: {databasePath} (SQLite {sqliteErrorCode}). See the inner exception for details."
            };
        }
    }
}
