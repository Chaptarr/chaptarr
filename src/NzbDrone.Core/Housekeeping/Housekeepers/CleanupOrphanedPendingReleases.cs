using Dapper;
using NLog;
using NzbDrone.Core.Download.Pending;
using NzbDrone.Core.Datastore;

namespace NzbDrone.Core.Housekeeping.Housekeepers
{
    public class CleanupOrphanedPendingReleases : IHousekeepingTask
    {
        private readonly IMainDatabase _database;
        private readonly Logger _logger;

        public CleanupOrphanedPendingReleases(IMainDatabase database, Logger logger)
        {
            _database = database;
            _logger = logger;
        }

        public void Clean()
        {
            using var mapper = _database.OpenConnection();
            var deleted = mapper.Query<PendingRelease>(@"DELETE FROM ""PendingReleases""
                             WHERE ""Id"" IN (
                             SELECT ""PendingReleases"".""Id"" FROM ""PendingReleases""
                             LEFT OUTER JOIN ""Authors""
                             ON ""PendingReleases"".""AuthorId"" = ""Authors"".""Id""
                             WHERE ""Authors"".""Id"" IS NULL)
                             RETURNING ""Id"", ""AuthorId""");
            foreach (var release in deleted)
            {
                _logger.Warn("Removed orphaned pending release {0}: author {1} no longer exists", release.Id, release.AuthorId);
            }
        }
    }
}
