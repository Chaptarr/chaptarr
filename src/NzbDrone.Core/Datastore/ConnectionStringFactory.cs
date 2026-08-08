using System;
using Microsoft.Data.Sqlite;
using Npgsql;
using NzbDrone.Common.EnvironmentInfo;
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

        public ConnectionStringFactory(IAppFolderInfo appFolderInfo, IConfigFileProvider configFileProvider)
        {
            _configFileProvider = configFileProvider;

            MainDbConnection = _configFileProvider.PostgresHost.IsNotNullOrWhiteSpace() ? GetPostgresConnectionString(_configFileProvider.PostgresMainDb) :
                GetConnectionString(appFolderInfo.GetDatabase());

            LogDbConnection = _configFileProvider.PostgresHost.IsNotNullOrWhiteSpace() ? GetPostgresConnectionString(_configFileProvider.PostgresLogDb) :
                GetConnectionString(appFolderInfo.GetLogDatabase());

            CacheDbConnection = _configFileProvider.PostgresHost.IsNotNullOrWhiteSpace() ? GetPostgresConnectionString(_configFileProvider.PostgresCacheDb) :
                GetConnectionString(appFolderInfo.GetCacheDatabase());
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
                Enlist = false
            };

            return new DatabaseConnectionInfo(DatabaseType.PostgreSQL, connectionBuilder.ConnectionString);
        }
    }
}
