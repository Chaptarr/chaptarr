using System;
using System.Data;
using System.Linq;
using System.Runtime.ExceptionServices;
using Microsoft.Data.Sqlite;
using Dapper;
using NLog;
using NzbDrone.Common.Disk;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Datastore.Migration;

namespace NzbDrone.Core.Datastore
{
    public interface IStagingDbContext
    {
        IDbConnection OpenConnection();
        void InitializeDatabase();
        Exception InitializationError { get; }
    }

    public class StagingDbContext : IStagingDbContext
    {
        private readonly IAppFolderInfo _appFolderInfo;
        private readonly Logger _logger;
        private readonly string _connectionString;
        private readonly object _initializationLock = new object();
        private readonly TimeProvider _clock;
        private DateTimeOffset _nextInitializationAttempt;
        internal static readonly TimeSpan InitializationRetryInterval = TimeSpan.FromMinutes(1);

        public StagingDbContext(IAppFolderInfo appFolderInfo, Logger logger)
            : this(appFolderInfo, logger, TimeProvider.System)
        {
        }

        internal StagingDbContext(IAppFolderInfo appFolderInfo, Logger logger, TimeProvider clock)
        {
            _clock = clock;
            _appFolderInfo = appFolderInfo;
            _logger = logger;
            
            var dbPath = _appFolderInfo.GetStagingDatabase();
            var csb = new SqliteConnectionStringBuilder
            {
                DataSource = dbPath,
                Cache = SqliteCacheMode.Private,
                Pooling = true
            };
            _connectionString = csb.ConnectionString;
        }

        public Exception InitializationError { get; private set; }

        public IDbConnection OpenConnection()
        {
            if (InitializationError != null)
            {
                lock (_initializationLock)
                {
                    if (InitializationError != null)
                    {
                        if (_clock.GetUtcNow() < _nextInitializationAttempt)
                            ExceptionDispatchInfo.Capture(InitializationError).Throw();
                        InitializeDatabase();
                    }
                }
            }

            return OpenConnection(true);
        }

        private SqliteConnection OpenConnection(bool pooling)
        {
            var connection = new SqliteConnection(new SqliteConnectionStringBuilder(_connectionString) { Pooling = pooling }.ToString());
            try
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = @"
                    PRAGMA journal_mode=WAL;
                    PRAGMA synchronous=NORMAL;
                    PRAGMA busy_timeout=3000;
                ";
                command.ExecuteNonQuery();
                return connection;
            }
            catch
            {
                connection.Dispose();
                throw;
            }
        }

        public void InitializeDatabase()
        {
            try
            {
                InitializeDatabaseCore();
                InitializationError = null;
                _nextInitializationAttempt = default;
            }
            catch (Exception ex)
            {
                InitializationError = ex;
                _nextInitializationAttempt = _clock.GetUtcNow() + InitializationRetryInterval;
                throw;
            }
        }

        private void InitializeDatabaseCore()
        {
            _logger.Info("Initializing staging database");
            
            using (var conn = OpenConnection(false))
            {
                // Check SQLite version
                var version = conn.ExecuteScalar<string>("SELECT sqlite_version()");
                _logger.Info("SQLite version: {0}", version);
                
                var versionParts = version.Split('.');
                var major = int.Parse(versionParts[0]);
                var minor = int.Parse(versionParts[1]);
                var patch = versionParts.Length > 2 ? int.Parse(versionParts[2]) : 0;
                
                // SQLite 3.25.0 introduced window functions (ROW_NUMBER, etc)
                // SQLite 3.35.0 introduced RETURNING clause for UPDATE statements
                if (major < 3 || (major == 3 && minor < 35))
                {
                    throw new InvalidOperationException($"SQLite version {version} is too old. Minimum required is 3.35.0 for RETURNING clause support in UPDATE statements.");
                }
                
                // Create tables
                conn.Execute(@"
                    CREATE TABLE IF NOT EXISTS ingest_queue(
                        id INTEGER PRIMARY KEY,
                        path TEXT NOT NULL UNIQUE,
                        mtime_ns INTEGER NOT NULL,
                        size_bytes INTEGER NOT NULL,
                        tags_json TEXT NOT NULL,
                        duration_seconds INTEGER,
                        status TEXT NOT NULL DEFAULT 'queued',
                        attempts INTEGER NOT NULL DEFAULT 0,
                        err TEXT,
                        created_at INTEGER NOT NULL,
                        updated_at INTEGER NOT NULL
                    );
                    
                    CREATE INDEX IF NOT EXISTS ix_ingest_status ON ingest_queue(status);
                    CREATE INDEX IF NOT EXISTS ix_ingest_status_path ON ingest_queue(status, path);
                    CREATE INDEX IF NOT EXISTS ix_ingest_status_created ON ingest_queue(status, created_at);
                    CREATE INDEX IF NOT EXISTS ix_ingest_updated ON ingest_queue(updated_at);
                    
                    -- Keep outcomes until the scan has consumed them and verified the main-database handoff.
                    CREATE TABLE IF NOT EXISTS import_results(
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        queue_item_id INTEGER NOT NULL,
                        path TEXT NOT NULL,
                        outcome TEXT NOT NULL CHECK(outcome IN ('imported', 'unmapped', 'failed', 'ignored', 'alreadylinked')),
                        book_id INTEGER,
                        author_id INTEGER,
                        quality TEXT,
                        error_message TEXT,
                        created_at INTEGER NOT NULL,
                        FOREIGN KEY(queue_item_id) REFERENCES ingest_queue(id)
                    );
                    
                    CREATE INDEX IF NOT EXISTS ix_results_queue_item ON import_results(queue_item_id);
                    CREATE INDEX IF NOT EXISTS ix_results_outcome ON import_results(outcome);
                    CREATE INDEX IF NOT EXISTS ix_results_created ON import_results(created_at);
                    
                    -- Command summaries are now consumed in memory before verified queue cleanup.
                    DROP TABLE IF EXISTS staging_metadata;
                ");

                // Backfill/upgrade: ensure new columns exist for older staging.db files.
                // We avoid schema migrations; staging.db is designed to be durable but disposable.
                try
                {
                    var cols = conn.Query<string>("SELECT name FROM pragma_table_info('ingest_queue')").ToList();
                    if (!cols.Any(c => c.Equals("duration_seconds", StringComparison.OrdinalIgnoreCase)))
                    {
                        conn.Execute("ALTER TABLE ingest_queue ADD COLUMN duration_seconds INTEGER;");
                        _logger.Info("[STAGING-DB] Added ingest_queue.duration_seconds column");
                    }
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "[STAGING-DB] Failed to ensure ingest_queue.duration_seconds column exists");
                    throw;
                }

                RepairImportResultsSchema(conn);

                // Safety: if the app crashed/restarted mid-import, queue items can be left in status='in_progress'.
                // On startup (when no handlers are actively processing), re-queue them so future scans can continue.
                try
                {
                    var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    var recovered = conn.Execute(@"
                        UPDATE ingest_queue
                        SET status = 'queued',
                            err = COALESCE(err, 'RECOVERED_AFTER_STARTUP'),
                            updated_at = @now
                        WHERE status = 'in_progress';
                    ", new { now });

                    if (recovered > 0)
                    {
                        _logger.Warn("[STAGING-DB] Re-queued {0} in_progress items from a previous run", recovered);
                    }
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "[STAGING-DB] Failed to recover in_progress items on startup");
                    throw;
                }
                
                _logger.Info("Staging database initialized successfully");
            }
        }

        private void RepairImportResultsSchema(IDbConnection conn)
        {
            try
            {
                var tableSql = conn.ExecuteScalar<string>("SELECT sql FROM sqlite_master WHERE type = 'table' AND name = 'import_results';");
                if (string.IsNullOrWhiteSpace(tableSql) ||
                    (tableSql.IndexOf("'ignored'", StringComparison.OrdinalIgnoreCase) >= 0 &&
                     tableSql.IndexOf("'alreadylinked'", StringComparison.OrdinalIgnoreCase) >= 0 &&
                     tableSql.IndexOf("AUTOINCREMENT", StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    return;
                }

                _logger.Info("[STAGING-DB] Upgrading import results for supported outcomes and non-reusable result IDs");

                using (var transaction = conn.BeginTransaction())
                {
                    conn.Execute("ALTER TABLE import_results RENAME TO import_results_old;", transaction: transaction);

                    conn.Execute(@"
                        CREATE TABLE import_results(
                            id INTEGER PRIMARY KEY AUTOINCREMENT,
                            queue_item_id INTEGER NOT NULL,
                            path TEXT NOT NULL,
                            outcome TEXT NOT NULL CHECK(outcome IN ('imported', 'unmapped', 'failed', 'ignored', 'alreadylinked')),
                            book_id INTEGER,
                            author_id INTEGER,
                            quality TEXT,
                            error_message TEXT,
                            created_at INTEGER NOT NULL,
                            FOREIGN KEY(queue_item_id) REFERENCES ingest_queue(id)
                        );
                    ", transaction: transaction);

                    conn.Execute(@"
                        INSERT INTO import_results (id, queue_item_id, path, outcome, book_id, author_id, quality, error_message, created_at)
                        SELECT id, queue_item_id, path, outcome, book_id, author_id, quality, error_message, created_at
                        FROM import_results_old;
                    ", transaction: transaction);

                    conn.Execute("DROP TABLE import_results_old;", transaction: transaction);
                    transaction.Commit();
                }

                conn.Execute(@"
                    CREATE INDEX IF NOT EXISTS ix_results_queue_item ON import_results(queue_item_id);
                    CREATE INDEX IF NOT EXISTS ix_results_outcome ON import_results(outcome);
                    CREATE INDEX IF NOT EXISTS ix_results_created ON import_results(created_at);
                ");
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "[STAGING-DB] Failed to upgrade import_results schema");
                throw;
            }
        }
    }
}
