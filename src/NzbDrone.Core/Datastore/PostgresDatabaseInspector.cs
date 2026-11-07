using System.Data.Common;
using Npgsql;

namespace NzbDrone.Core.Datastore
{
    public sealed record PostgresDatabaseState(bool Exists, DatabaseLineage Lineage, bool HasBookshelfMigrationMarker);

    public interface IPostgresDatabaseInspector
    {
        PostgresDatabaseState Inspect(string connectionString);
    }

    public class PostgresDatabaseInspector : IPostgresDatabaseInspector
    {
        public PostgresDatabaseState Inspect(string connectionString)
        {
            var connectionBuilder = new NpgsqlConnectionStringBuilder(connectionString)
            {
                Pooling = false
            };

            using var connection = new NpgsqlConnection(connectionBuilder.ConnectionString);
            try
            {
                connection.Open();
            }
            catch (PostgresException exception) when (exception.SqlState == "3D000")
            {
                return new PostgresDatabaseState(false, DatabaseLineage.Empty, false);
            }

            var lineage = DatabaseLineageGuard.Detect(connection, DatabaseType.PostgreSQL);
            var hasBookshelfMigrationMarker = lineage == DatabaseLineage.Chaptarr && HasBookshelfMigrationMarker(connection);

            return new PostgresDatabaseState(true, lineage, hasBookshelfMigrationMarker);
        }

        private static bool HasBookshelfMigrationMarker(DbConnection connection)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT EXISTS (SELECT 1 FROM \"Config\" WHERE \"Key\" = @key AND lower(\"Value\") = 'true');";
            var parameter = command.CreateParameter();
            parameter.ParameterName = "key";
            parameter.Value = BookshelfDatabaseImporter.BookshelfNGMigrationMarkerKey;
            command.Parameters.Add(parameter);

            return (bool)command.ExecuteScalar();
        }
    }
}
