using System;
using System.IO;
using Microsoft.Data.Sqlite;
using Npgsql;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Common.Exceptions;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Configuration;

namespace NzbDrone.Core.Datastore
{
    public interface IConnectionStringFactory
    {
        DatabaseConnectionInfo MainDbConnection { get; }
        DatabaseConnectionInfo LogDbConnection { get; }
        DatabaseConnectionInfo CacheDbConnection { get; }
        string GetDatabasePath(string connectionString);
    }

    public class ConnectionStringFactory : IConnectionStringFactory
    {
        private readonly IConfigFileProvider _configFileProvider;
        private readonly IPostgresDatabaseInspector _postgresDatabaseInspector;

        public ConnectionStringFactory(
            IAppFolderInfo appFolderInfo,
            IConfigFileProvider configFileProvider,
            IPostgresDatabaseInspector postgresDatabaseInspector)
        {
            _configFileProvider = configFileProvider;
            _postgresDatabaseInspector = postgresDatabaseInspector;

            if (_configFileProvider.PostgresHost.IsNotNullOrWhiteSpace())
            {
                var selection = BookshelfPostgresDatabaseSelector.Resolve(
                    _configFileProvider.PostgresMainDb,
                    _configFileProvider.PostgresLogDb,
                    _configFileProvider.PostgresCacheDb,
                    _configFileProvider.IsPostgresMainDbExplicitlyConfigured(),
                    _configFileProvider.IsPostgresLogDbExplicitlyConfigured(),
                    _configFileProvider.IsPostgresCacheDbExplicitlyConfigured(),
                    InspectPostgresDatabase);

                MainDbConnection = GetPostgresConnectionString(selection.MainDb);
                LogDbConnection = GetPostgresConnectionString(selection.LogDb);
                CacheDbConnection = GetPostgresConnectionString(selection.CacheDb);
            }
            else
            {
                MainDbConnection = GetConnectionString(appFolderInfo.GetDatabase());
                LogDbConnection = GetConnectionString(appFolderInfo.GetLogDatabase());
                CacheDbConnection = GetConnectionString(appFolderInfo.GetCacheDatabase());
            }
        }

        public DatabaseConnectionInfo MainDbConnection { get; private set; }
        public DatabaseConnectionInfo LogDbConnection { get; private set; }
        public DatabaseConnectionInfo CacheDbConnection { get; private set; }

        public string GetDatabasePath(string connectionString)
        {
            var connectionBuilder = new SqliteConnectionStringBuilder(connectionString);
            return connectionBuilder.DataSource;
        }

        private static DatabaseConnectionInfo GetConnectionString(string dbPath)
        {
            // Microsoft.Data.Sqlite ignores many System.Data.SQLite-specific connection string keys.
            // We set the essentials here and rely on explicit PRAGMAs where needed.
            var csb = new SqliteConnectionStringBuilder
            {
                DataSource = dbPath,
                Cache = SqliteCacheMode.Private,
                Pooling = true
            };

            // Note: DateTimeKind=Utc is not supported by Microsoft.Data.Sqlite. UTC is enforced via Dapper TypeHandlers.
            // Journal mode (WAL/Truncate) should be set via PRAGMA after opening connections where required.

            return new DatabaseConnectionInfo(DatabaseType.SQLite, csb.ConnectionString);
        }

        private DatabaseConnectionInfo GetPostgresConnectionString(string dbName)
        {
            var connectionBuilder = new NpgsqlConnectionStringBuilder
            {
                Database = dbName,
                Host = _configFileProvider.PostgresHost,
                Username = _configFileProvider.PostgresUser,
                Password = _configFileProvider.PostgresPassword,
                Port = _configFileProvider.PostgresPort,
                Enlist = false,

                // Nothing in the application relies on per-session state, so skip the
                // reset round-trip every time a pooled connection is returned.
                NoResetOnClose = _configFileProvider.PostgresNoResetOnClose
            };

            ApplyPostgresTls(connectionBuilder);

            return new DatabaseConnectionInfo(DatabaseType.PostgreSQL, connectionBuilder.ConnectionString);
        }

        private PostgresDatabaseState InspectPostgresDatabase(string dbName)
        {
            if (string.IsNullOrWhiteSpace(dbName))
            {
                return new PostgresDatabaseState(false, DatabaseLineage.Empty, false);
            }

            return _postgresDatabaseInspector.Inspect(GetPostgresConnectionString(dbName).ConnectionString);
        }

        private void ApplyPostgresTls(NpgsqlConnectionStringBuilder connectionBuilder)
        {
            var sslMode = _configFileProvider.PostgresSslMode;
            var rootCert = _configFileProvider.PostgresRootCert;

            if (sslMode.IsNotNullOrWhiteSpace())
            {
                connectionBuilder.SslMode = ParseSslMode(sslMode);
            }

            if (rootCert.IsNotNullOrWhiteSpace())
            {
                if (!File.Exists(rootCert))
                {
                    throw new ChaptarrStartupException("The configured PostgreSQL root certificate file was not found: {0}", rootCert);
                }

                connectionBuilder.RootCertificate = rootCert;
            }
        }

        private static SslMode ParseSslMode(string value)
        {
            var normalized = value.Trim().Replace("-", string.Empty).Replace("_", string.Empty);

            if (Enum.TryParse<SslMode>(normalized, true, out var mode) && Enum.IsDefined(typeof(SslMode), mode))
            {
                return mode;
            }

            throw new ChaptarrStartupException("Unsupported PostgreSQL SSL mode '{0}'. Use one of: disable, allow, prefer, require, verify-ca, verify-full.", value);
        }
    }
}
