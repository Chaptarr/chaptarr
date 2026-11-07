using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;

namespace ChaptarrNG.Installer.Pipeline;

public sealed class SqliteDatabaseVersionGuard : IDisposable
{
    private readonly IReadOnlyList<(SqliteConnection Connection, long InitialVersion)> _connections;

    public SqliteDatabaseVersionGuard(string dataDirectory)
    {
        var connections = new List<(SqliteConnection Connection, long InitialVersion)>();
        try
        {
            foreach (var databaseName in BookshelfDatabaseFiles.GetPopulatedDatabaseNames(dataDirectory))
            {
                var databasePath = Path.Combine(dataDirectory, databaseName);
                var connectionString = new SqliteConnectionStringBuilder
                {
                    DataSource = databasePath,
                    Mode = SqliteOpenMode.ReadOnly,
                    Pooling = false
                }.ConnectionString;

                var connection = new SqliteConnection(connectionString);
                connection.Open();
                connections.Add((connection, ReadVersion(connection)));
            }

            if (connections.Count == 0)
            {
                throw new FileNotFoundException("No populated BookshelfNG SQLite database was available to monitor during the installer upgrade.");
            }

            _connections = connections;
        }
        catch
        {
            foreach (var (connection, _) in connections)
            {
                connection.Dispose();
            }

            throw;
        }
    }

    public bool HasChanged => _connections.Any(item => ReadVersion(item.Connection) != item.InitialVersion);

    public void Dispose()
    {
        foreach (var (connection, _) in _connections)
        {
            connection.Dispose();
        }
    }

    private static long ReadVersion(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA data_version;";
        return Convert.ToInt64(command.ExecuteScalar());
    }
}
