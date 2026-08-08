using Dapper;
using NzbDrone.Core.Datastore;

namespace NzbDrone.Core.Housekeeping.Housekeepers
{
    public class FixNullProviderSettings : IHousekeepingTask
    {
        private readonly IMainDatabase _database;

        public FixNullProviderSettings(IMainDatabase database)
        {
            _database = database;
        }

        public void Clean()
        {
            using var conn = _database.OpenConnection();

            // Provider tables with nullable Settings
            conn.Execute(@"UPDATE ""Indexers"" SET ""Settings"" = NULL WHERE TRIM(""Settings"") = 'null'");
            conn.Execute(@"UPDATE ""DownloadClients"" SET ""Settings"" = NULL WHERE TRIM(""Settings"") = 'null'");
            conn.Execute(@"UPDATE ""ImportLists"" SET ""Settings"" = NULL WHERE TRIM(""Settings"") = 'null'");

            // Provider tables with non-nullable Settings
            conn.Execute(@"UPDATE ""Notifications"" SET ""Settings"" = '{}' WHERE TRIM(""Settings"") = 'null'");
            conn.Execute(@"UPDATE ""Metadata"" SET ""Settings"" = '{}' WHERE TRIM(""Settings"") = 'null'");
        }
    }
}
