using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Linq;
using Dapper;
using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Messaging.Events;

namespace NzbDrone.Core.Books
{
    public interface IAuthorRepository : IBasicRepository<Author>
    {
        Author Update(Author author, IDbConnection connection, IDbTransaction transaction);
        List<Author> FindExisting(IEnumerable<int> ids);
        List<int> GetDueInitialFormatSync(DateTime now);
        bool AuthorPathExists(string path);
        Author FindByGoodreadsId(string goodreadsId);
        Author FindByHardcoverId(string hardcoverId);
        Author FindByAudnexusId(string audnexusId);
        Author FindByOpenLibraryId(string openLibraryId);
        Author FindByGoogleBooksId(string googleBooksAuthorId);
        List<int> GetAuthorIdsByMetadataProfileId(int metadataProfileId);
        List<Author> AllAuthorPaths();
        Dictionary<int, List<int>> AllAuthorTags();
        void UpdateLastSelectedMediaType(int authorId, string mediaType);
        
        // Import claim methods
        bool TryClaimAuthorImport(string providerId, int leaseSec = 300);
        void ReleaseAuthorImportClaim(string providerId);
        int DeleteExpiredClaims();
    }

    public class AuthorRepository : BasicRepository<Author>, IAuthorRepository
    {
        private readonly Logger _logger;

        public AuthorRepository(IMainDatabase database,
                                IEventAggregator eventAggregator,
                                Logger logger)
            : base(database, eventAggregator)
        {
            _logger = logger;
        }

        protected override SqlBuilder Builder() => new SqlBuilder(_database.DatabaseType);

        protected override List<Author> Query(SqlBuilder builder) => Query(_database, builder).ToList();

        public static IEnumerable<Author> Query(IDatabase database, SqlBuilder builder)
        {
            return database.Query<Author>(builder);
        }

        public Author Update(Author author, IDbConnection connection, IDbTransaction transaction)
        {
            UpdateMany(new[] { author }, connection, transaction);
            return author;
        }

        public List<Author> FindExisting(IEnumerable<int> ids) => FindMany(ids);

        private const int InitialSetupRetryBatchSize = 100;
        private const int InitialSetupRetryTimeoutSeconds = 30;
        private const int SlowInitialSetupRetryMilliseconds = 100;

        private sealed class InitialSetupRetryRow
        {
            public int? Id { get; set; }
            public int PendingCount { get; set; }
        }

        public List<int> GetDueInitialFormatSync(DateTime now)
        {
            var timer = Stopwatch.StartNew();
            var postgres = _database.DatabaseType == DatabaseType.PostgreSQL;
            var dueTime = postgres
                ? "(\"AddOptions\"::jsonb ->> 'initialFormatSyncNotBefore')::timestamptz"
                : "julianday(json_extract(\"AddOptions\", '$.initialFormatSyncNotBefore'))";
            var eligible = postgres
                ? "(\"AddOptions\"::jsonb ->> 'awaitingMetadata')::boolean IS NOT TRUE AND (\"AddOptions\"::jsonb ->> 'metadataUnavailable')::boolean IS NOT TRUE"
                : "COALESCE(json_extract(\"AddOptions\", '$.awaitingMetadata'), 0) = 0 AND COALESCE(json_extract(\"AddOptions\", '$.metadataUnavailable'), 0) = 0";
            var nowValue = postgres ? "@now" : "julianday(@now)";
            // Filter persisted setup scalars in the database. Remaining due requests
            // stay eligible for the next minute; no author or catalog objects hydrate.
            var sql = $@"
                WITH pending AS (
                    SELECT ""Id"", ""AddOptions"" FROM ""Authors"" WHERE ""AddOptions"" IS NOT NULL
                ), eligible AS (
                    SELECT ""Id"", {dueTime} AS ""Due"" FROM pending WHERE {eligible}
                ), due AS (
                    SELECT ""Id"", ""Due"" FROM eligible WHERE ""Due"" <= {nowValue}
                    ORDER BY ""Due"", ""Id"" LIMIT @limit
                )
                SELECT due.""Id"", summary.""PendingCount""
                FROM (SELECT COUNT(*) AS ""PendingCount"" FROM pending) AS summary
                LEFT JOIN due ON 1 = 1
                ORDER BY due.""Due"", due.""Id""";
            using var connection = _database.OpenConnection();
            var rows = connection.Query<InitialSetupRetryRow>(sql, new { now, limit = InitialSetupRetryBatchSize },
                commandTimeout: InitialSetupRetryTimeoutSeconds).ToList();
            var ids = rows.Where(row => row.Id.HasValue).Select(row => row.Id.Value).ToList();
            var pendingCount = rows.FirstOrDefault()?.PendingCount ?? 0;
            if (pendingCount > 0)
                _logger.Log(timer.ElapsedMilliseconds >= SlowInitialSetupRetryMilliseconds ? LogLevel.Info : LogLevel.Debug,
                    "Initial format-sync retry: {0} due in this batch, {1} pending; elapsed {2} ms", ids.Count, pendingCount, timer.ElapsedMilliseconds);
            return ids;
        }

        public bool AuthorPathExists(string path)
        {
            return Query(c => c.Path == path || c.AudiobookPath == path || c.EbookPath == path).Any();
        }

        public Author FindByGoodreadsId(string goodreadsId)
        {
            return Query(a => a.GoodreadsAuthorId == goodreadsId).SingleOrDefault();
        }

        public Author FindByHardcoverId(string hardcoverId)
        {
            return Query(a => a.HardcoverAuthorId == hardcoverId).SingleOrDefault();
        }

        public Author FindByAudnexusId(string audnexusId)
        {
            return Query(a => a.AudnexusAuthorId == audnexusId).SingleOrDefault();
        }

        public Author FindByOpenLibraryId(string openLibraryId)
        {
            return Query(a => a.OpenLibraryAuthorId == openLibraryId).SingleOrDefault();
        }

        public Author FindByGoogleBooksId(string googleBooksAuthorId)
        {
            return Query(a => a.GoogleBooksAuthorId == googleBooksAuthorId).SingleOrDefault();
        }

        public List<int> GetAuthorIdsByMetadataProfileId(int metadataProfileId)
        {
            if (metadataProfileId <= 0)
            {
                return new List<int>();
            }

            using (var conn = _database.OpenConnection())
            {
                var sql = "SELECT \"Id\" FROM \"Authors\" WHERE \"MetadataProfileId\" = @id OR \"AudiobookMetadataProfileId\" = @id OR \"EbookMetadataProfileId\" = @id";
                return conn.Query<int>(sql, new { id = metadataProfileId }).ToList();
            }
        }

        public List<Author> AllAuthorPaths()
        {
            using (var conn = _database.OpenConnection())
            {
                var strSql = "SELECT \"Id\", \"Name\", \"Path\", \"AudiobookPath\", \"EbookPath\" FROM \"Authors\"";
                return conn.Query<Author>(strSql).ToList();
            }
        }

        public Dictionary<int, List<int>> AllAuthorTags()
        {
            using (var conn = _database.OpenConnection())
            {
                var strSql = "SELECT \"Id\" AS \"Key\", \"Tags\" AS \"Value\" FROM \"Authors\" WHERE \"Tags\" IS NOT NULL";
                return conn.Query<KeyValuePair<int, List<int>>>(strSql).ToDictionary(x => x.Key, x => x.Value);
            }
        }


        public void UpdateLastSelectedMediaType(int authorId, string mediaType)
        {
            var sql = "UPDATE \"Authors\" SET \"LastSelectedMediaType\" = @mediaType WHERE \"Id\" = @authorId";
            using (var conn = _database.OpenConnection())
            {
                conn.Execute(sql, new { mediaType = mediaType, authorId = authorId });
            }
        }

        public bool TryClaimAuthorImport(string providerId, int leaseSec = 300)
        {
            using (var conn = _database.OpenConnection())
            {
                try
                {
                    var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    var sql = @"
                        INSERT INTO ""AuthorImportClaims"" (""ProviderId"", ""ClaimedAt"", ""LeaseSec"")
                        VALUES (@providerId, @now, @leaseSec)
                        ON CONFLICT(""ProviderId"") DO NOTHING;
                    ";
                    
                    var affected = conn.Execute(sql, new { providerId, now, leaseSec });
                    return affected > 0;
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "Failed to claim author import for provider ID: {0}", providerId);
                    return false;
                }
            }
        }

        public void ReleaseAuthorImportClaim(string providerId)
        {
            using (var conn = _database.OpenConnection())
            {
                var sql = @"DELETE FROM ""AuthorImportClaims"" WHERE ""ProviderId"" = @providerId";
                conn.Execute(sql, new { providerId });
            }
        }

        public int DeleteExpiredClaims()
        {
            using (var conn = _database.OpenConnection())
            {
                var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                var sql = @"
                    DELETE FROM ""AuthorImportClaims"" 
                    WHERE @now > (""ClaimedAt"" + ""LeaseSec"");
                ";
                
                var deleted = conn.Execute(sql, new { now });
                if (deleted > 0)
                {
                    _logger.Debug("Cleaned {0} expired author import claims", deleted);
                }
                
                return deleted;
            }
        }
    }
}
