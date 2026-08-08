using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Dapper;
using Microsoft.Data.Sqlite;
using NLog;
using Npgsql;
using NzbDrone.Common.Disk;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Instrumentation;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Datastore;

namespace NzbDrone.Core.Reset
{
    public interface IFactoryResetService
    {
        Task ResetEverythingAsync(CancellationToken cancellationToken = default);
    }

    public class FactoryResetService : IFactoryResetService
    {
        private static readonly SemaphoreSlim ResetGate = new SemaphoreSlim(1, 1);

        private readonly IAppFolderInfo _appFolderInfo;
        private readonly IDiskProvider _diskProvider;
        private readonly IConnectionStringFactory _connectionStringFactory;
        private readonly IConfigFileProvider _configFileProvider;
        private readonly Logger _logger;

        public FactoryResetService(IAppFolderInfo appFolderInfo,
                                  IDiskProvider diskProvider,
                                  IConnectionStringFactory connectionStringFactory,
                                  IConfigFileProvider configFileProvider,
                                  Logger logger)
        {
            _appFolderInfo = appFolderInfo;
            _diskProvider = diskProvider;
            _connectionStringFactory = connectionStringFactory;
            _configFileProvider = configFileProvider;
            _logger = logger;
        }

        public async Task ResetEverythingAsync(CancellationToken cancellationToken = default)
        {
            await ResetGate.WaitAsync(cancellationToken);

            try
            {
                _logger.Warn("[FACTORY-RESET] Starting factory reset: wiping databases, settings, and caches");

                // From here until the process restarts, live services would be querying a schema
                // that is being dropped. Flip the process-wide gate so the HTTP pipeline answers
                // 503 instead of surfacing raw "no such table" errors to browsers and logs.
                FactoryResetState.MarkResetting();

                // Capture host-level connectivity settings so users do not get locked out after reset.
                // All other settings (including auth + API key) will be regenerated.
                var preservedHostConfig = CapturePreservedHostConfig();

                var mainDb = _connectionStringFactory.MainDbConnection;
                var logDb = _connectionStringFactory.LogDbConnection;
                var cacheDb = _connectionStringFactory.CacheDbConnection;

                cancellationToken.ThrowIfCancellationRequested();
                ResetDatabaseSchema("main", mainDb);

                cancellationToken.ThrowIfCancellationRequested();
                ResetDatabaseSchema("log", logDb);

                cancellationToken.ThrowIfCancellationRequested();
                ResetDatabaseSchema("cache", cacheDb);

                cancellationToken.ThrowIfCancellationRequested();
                ResetStagingDatabase();

                cancellationToken.ThrowIfCancellationRequested();
                ResetAppDataFiles(preservedHostConfig);

                _logger.Warn("[FACTORY-RESET] Factory reset finished successfully");
            }
            finally
            {
                ResetGate.Release();
            }
        }

        private Dictionary<string, object> CapturePreservedHostConfig()
        {
            // These are required for the UI to stay reachable on restart. Avoid persisting external DB secrets
            // (only preserve PostgreSQL settings that were already persisted in config.xml).
            // Note: UrlBase includes a leading '/' when set ("" otherwise). Persisting it as-is is safe.
            var preserved = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
            {
                { nameof(IConfigFileProvider.BindAddress), _configFileProvider.BindAddress },
                { nameof(IConfigFileProvider.Port), _configFileProvider.Port },
                { nameof(IConfigFileProvider.SslPort), _configFileProvider.SslPort },
                { nameof(IConfigFileProvider.EnableSsl), _configFileProvider.EnableSsl },
                { nameof(IConfigFileProvider.SslCertPath), _configFileProvider.SslCertPath ?? string.Empty },
                { nameof(IConfigFileProvider.SslCertPassword), _configFileProvider.SslCertPassword ?? string.Empty },
                { nameof(IConfigFileProvider.UrlBase), _configFileProvider.UrlBase ?? string.Empty }
            };

            // If this instance is using PostgreSQL and the connection settings were persisted in config.xml,
            // preserve them during reset so the app doesn't silently fall back to SQLite on restart.
            TryPreservePostgresConnectionSettingsFromConfigFile(preserved);

            return preserved;
        }

        private void TryPreservePostgresConnectionSettingsFromConfigFile(Dictionary<string, object> preservedHostConfig)
        {
            try
            {
                if (_connectionStringFactory?.MainDbConnection?.DatabaseType != DatabaseType.PostgreSQL)
                {
                    return;
                }

                var configPath = _appFolderInfo.GetConfigPath();
                if (!_diskProvider.FileExists(configPath))
                {
                    return;
                }

                var contents = _diskProvider.ReadAllText(configPath);
                if (contents.IsNullOrWhiteSpace())
                {
                    return;
                }

                var xDoc = XDocument.Parse(contents);
                var config = xDoc.Descendants(ConfigFileProvider.CONFIG_ELEMENT_NAME).SingleOrDefault();
                if (config == null)
                {
                    return;
                }

                var keys = new[]
                {
                    nameof(IConfigFileProvider.PostgresHost),
                    nameof(IConfigFileProvider.PostgresPort),
                    nameof(IConfigFileProvider.PostgresUser),
                    nameof(IConfigFileProvider.PostgresPassword),
                    nameof(IConfigFileProvider.PostgresMainDb),
                    nameof(IConfigFileProvider.PostgresLogDb),
                    nameof(IConfigFileProvider.PostgresCacheDb)
                };

                var preservedCount = 0;
                foreach (var key in keys)
                {
                    var value = config.Descendants(key).FirstOrDefault()?.Value?.Trim();
                    if (value.IsNullOrWhiteSpace())
                    {
                        continue;
                    }

                    preservedHostConfig[key] = value;
                    preservedCount++;
                }

                if (preservedCount > 0)
                {
                    _logger.Warn("[FACTORY-RESET] Preserving PostgreSQL connection settings from config.xml to avoid switching to SQLite after reset");
                }
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "[FACTORY-RESET] Failed to preserve PostgreSQL connection settings from config.xml");
            }
        }

        private void ResetDatabaseSchema(string label, DatabaseConnectionInfo connectionInfo)
        {
            if (connectionInfo == null)
            {
                _logger.Warn("[FACTORY-RESET] Skipping {0} database reset: connection info is null", label);
                return;
            }

            _logger.Warn("[FACTORY-RESET] Resetting {0} database schema ({1})", label, connectionInfo.DatabaseType);

            if (connectionInfo.DatabaseType == DatabaseType.SQLite)
            {
                // Prefer deleting the file family outright: a DROP-based wipe cannot remove an
                // FTS5 virtual table whose shadow tables were dropped first ("vtable constructor
                // failed"), and file deletion side-steps every such in-schema failure mode.
                // Fall back to a schema wipe only when the file cannot be deleted (Windows locks).
                if (TryDeleteSqliteDatabase(label, connectionInfo.ConnectionString))
                {
                    return;
                }

                ResetSqliteSchema(connectionInfo.ConnectionString);
                return;
            }

            ResetPostgresSchema(connectionInfo.ConnectionString);
        }

        private bool TryDeleteSqliteDatabase(string label, string connectionString)
        {
            try
            {
                var dataSource = new SqliteConnectionStringBuilder(connectionString).DataSource;
                if (dataSource.IsNullOrWhiteSpace() || !_diskProvider.FileExists(dataSource))
                {
                    return false;
                }

                // Release this process's pooled handles so the files can be deleted on every OS.
                SqliteConnection.ClearAllPools();

                TryDeleteSqliteFileFamily(dataSource);

                if (_diskProvider.FileExists(dataSource))
                {
                    _logger.Warn("[FACTORY-RESET] Could not delete {0} database file; falling back to schema wipe: {1}", label, dataSource);
                    return false;
                }

                _logger.Warn("[FACTORY-RESET] Deleted {0} database file family: {1}", label, dataSource);
                return true;
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "[FACTORY-RESET] Failed to delete {0} database file; falling back to schema wipe", label);
                return false;
            }
        }

        private void ResetStagingDatabase()
        {
            // staging.db is durable but disposable; wipe it as part of a full reset.
            var stagingPath = System.IO.Path.Combine(_appFolderInfo.AppDataFolder, "staging.db");

            _logger.Warn("[FACTORY-RESET] Resetting staging database: {0}", stagingPath);

            try
            {
                if (_diskProvider.FileExists(stagingPath))
                {
                    // Prefer deletion (staging is SQLite-only), but fall back to schema reset if deletion fails (Windows locks).
                    TryDeleteSqliteFileFamily(stagingPath);
                }
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "[FACTORY-RESET] Failed to delete staging.db file; attempting schema reset instead");
            }

            try
            {
                var csb = new SqliteConnectionStringBuilder
                {
                    DataSource = stagingPath,
                    Cache = SqliteCacheMode.Private,
                    Pooling = true
                };
                ResetSqliteSchema(csb.ConnectionString);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "[FACTORY-RESET] Failed to reset staging.db schema");
                throw;
            }
        }

        private void ResetAppDataFiles(Dictionary<string, object> preservedHostConfig)
        {
            // Wipe app-level caches.
            TryDeleteFolder(_appFolderInfo.GetMediaCoverPath(), recursive: true);
            TryDeleteFolder(_appFolderInfo.GetDataProtectionPath(), recursive: true);

            // Try to clear logs, but do not fail reset if files are locked (especially on Windows).
            TryDeleteFolder(_appFolderInfo.GetLogFolder(), recursive: true, allowFailure: true);
            TryDeleteFolder(_appFolderInfo.GetUpdateLogFolder(), recursive: true, allowFailure: true);

            // Wipe config.xml (settings + API key + auth settings) then restore host connectivity settings.
            var configPath = _appFolderInfo.GetConfigPath();
            try
            {
                if (_diskProvider.FileExists(configPath))
                {
                    _diskProvider.DeleteFile(configPath);
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "[FACTORY-RESET] Failed to delete config file: {0}", configPath);
                throw;
            }

            // Recreate config file with defaults + preserved host settings.
            try
            {
                _configFileProvider.SaveConfigDictionary(preservedHostConfig);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "[FACTORY-RESET] Failed to recreate config file after reset");
                throw;
            }
        }

        private static void ResetSqliteSchema(string connectionString)
        {
            using var conn = new SqliteConnection(connectionString);
            conn.Open();

            // Best-effort pragmas; reset should keep moving even if some fail.
            try
            {
                conn.Execute("PRAGMA foreign_keys=OFF; PRAGMA busy_timeout=5000;");
            }
            catch
            {
                // ignore
            }

            var objects = conn.Query<(string Type, string Name, string Sql)>(@"
                SELECT type as Type, name as Name, sql as Sql
                FROM sqlite_master
                WHERE name NOT LIKE 'sqlite_%'
                  AND type IN ('table','view','index','trigger');
            ").ToList();

            // Drop in dependency-safe order.
            foreach (var obj in objects.Where(o => string.Equals(o.Type, "view", StringComparison.OrdinalIgnoreCase)))
            {
                conn.Execute($"DROP VIEW IF EXISTS {QuoteSqliteIdentifier(obj.Name)};");
            }

            foreach (var obj in objects.Where(o => string.Equals(o.Type, "trigger", StringComparison.OrdinalIgnoreCase)))
            {
                conn.Execute($"DROP TRIGGER IF EXISTS {QuoteSqliteIdentifier(obj.Name)};");
            }

            static bool IsVirtualTable((string Type, string Name, string Sql) o) =>
                o.Sql != null && o.Sql.TrimStart().StartsWith("CREATE VIRTUAL TABLE", StringComparison.OrdinalIgnoreCase);

            // Virtual tables (FTS5 etc.) MUST go before ordinary tables: their shadow tables
            // (name_data, name_config, ...) are listed in sqlite_master as plain tables, and
            // dropping a shadow first leaves an undroppable zombie vtable ("vtable constructor
            // failed") that also bricks later migrations. Dropping the vtable removes its shadows.
            var virtualTables = objects.Where(o => string.Equals(o.Type, "table", StringComparison.OrdinalIgnoreCase) && IsVirtualTable(o)).ToList();
            foreach (var obj in virtualTables)
            {
                conn.Execute($"DROP TABLE IF EXISTS {QuoteSqliteIdentifier(obj.Name)};");
            }

            var shadowPrefixes = virtualTables.Select(o => o.Name + "_").ToList();
            bool IsShadowOfDroppedVtable(string name) => shadowPrefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase));

            // Drop the migration bookkeeping table first among ordinary tables: if a later drop
            // fails mid-way, the restart's migrator must see a fresh database and rebuild
            // everything. Leaving VersionInfo behind with other tables gone would make it skip
            // the schema rebuild.
            foreach (var obj in objects.Where(o => string.Equals(o.Type, "table", StringComparison.OrdinalIgnoreCase) &&
                                                   !IsVirtualTable(o) &&
                                                   !IsShadowOfDroppedVtable(o.Name))
                                       .OrderByDescending(o => string.Equals(o.Name, "VersionInfo", StringComparison.OrdinalIgnoreCase)))
            {
                conn.Execute($"DROP TABLE IF EXISTS {QuoteSqliteIdentifier(obj.Name)};");
            }

            foreach (var obj in objects.Where(o => string.Equals(o.Type, "index", StringComparison.OrdinalIgnoreCase)))
            {
                conn.Execute($"DROP INDEX IF EXISTS {QuoteSqliteIdentifier(obj.Name)};");
            }

            try
            {
                conn.Execute("VACUUM;");
            }
            catch
            {
                // ignore
            }
        }

        private static void ResetPostgresSchema(string connectionString)
        {
            using var conn = new NpgsqlConnection(connectionString);
            conn.Open();

            // Ensure we only wipe the app schema inside the current database.
            // Postgres does not support DROP DATABASE within a connection to that database.
            conn.Execute(@"
                DROP SCHEMA IF EXISTS public CASCADE;
                CREATE SCHEMA public;
            ");
        }

        private void TryDeleteSqliteFileFamily(string dbPath)
        {
            // SQLite can create sidecar files under WAL mode.
            TryDeleteFile(dbPath + "-shm");
            TryDeleteFile(dbPath + "-wal");
            TryDeleteFile(dbPath + "-journal");
            TryDeleteFile(dbPath);
        }

        private void TryDeleteFile(string path)
        {
            try
            {
                if (_diskProvider.FileExists(path))
                {
                    _diskProvider.DeleteFile(path);
                }
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "[FACTORY-RESET] Failed to delete file: {0}", path);
            }
        }

        private void TryDeleteFolder(string path, bool recursive, bool allowFailure = false)
        {
            try
            {
                if (_diskProvider.FolderExists(path))
                {
                    _diskProvider.DeleteFolder(path, recursive);
                }
            }
            catch (Exception ex)
            {
                if (allowFailure)
                {
                    _logger.Debug(ex, "[FACTORY-RESET] Failed to delete folder (ignored): {0}", path);
                    return;
                }

                _logger.Error(ex, "[FACTORY-RESET] Failed to delete folder: {0}", path);
                throw;
            }
        }

        private static string QuoteSqliteIdentifier(string identifier)
        {
            if (identifier.IsNullOrWhiteSpace())
            {
                throw new ArgumentException("SQLite identifier cannot be empty", nameof(identifier));
            }

            return "\"" + identifier.Replace("\"", "\"\"") + "\"";
        }
    }
}
