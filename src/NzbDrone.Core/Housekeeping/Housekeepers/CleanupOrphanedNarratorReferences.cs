using Dapper;
using NzbDrone.Core.Datastore;

namespace NzbDrone.Core.Housekeeping.Housekeepers
{
    public class CleanupOrphanedNarratorReferences : IHousekeepingTask
    {
        private readonly IMainDatabase _database;

        public CleanupOrphanedNarratorReferences(IMainDatabase database)
        {
            _database = database;
        }

        public void Clean()
        {
            using var mapper = _database.OpenConnection();

            mapper.Execute(
                @"DELETE FROM ""BookNarratorLink""
                  WHERE ""NarratorId"" NOT IN (SELECT ""Id"" FROM ""Narrators"")
                     OR ""BookId"" NOT IN (SELECT ""Id"" FROM ""Books"")");

            mapper.Execute(
                @"DELETE FROM ""EditionNarratorLink""
                  WHERE ""NarratorId"" NOT IN (SELECT ""Id"" FROM ""Narrators"")
                     OR ""EditionId"" NOT IN (SELECT ""Id"" FROM ""Editions"")");

            mapper.Execute(
                @"UPDATE ""Books""
                  SET ""NarratorId"" = NULL
                  WHERE ""NarratorId"" IS NOT NULL
                    AND ""NarratorId"" NOT IN (SELECT ""Id"" FROM ""Narrators"")");

            mapper.Execute(
                @"UPDATE ""Books""
                  SET ""WantedNarratorId"" = NULL
                  WHERE ""WantedNarratorId"" IS NOT NULL
                    AND ""WantedNarratorId"" NOT IN (SELECT ""Id"" FROM ""Narrators"")");

            mapper.Execute(
                @"UPDATE ""Series""
                  SET ""PreferredNarratorId"" = NULL
                  WHERE ""PreferredNarratorId"" IS NOT NULL
                    AND ""PreferredNarratorId"" NOT IN (SELECT ""Id"" FROM ""Narrators"")");
        }
    }
}
