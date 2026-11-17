using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using Dapper;
using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Serializer;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Messaging.Events;

namespace NzbDrone.Core.Books
{
    public interface IAuthorRepository : IBasicRepository<Author>
    {
        Author Update(Author author, IDbConnection connection, IDbTransaction transaction);
        bool RemoveImageUrls(int authorId, IReadOnlyCollection<string> urls);
        bool UpdateSelectedPosterHash(int authorId, string hash, string expectedHash = null);
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
        private List<PropertyInfo> _updatePropertiesWithoutPosterChoice;

        public AuthorRepository(IMainDatabase database,
                                IEventAggregator eventAggregator,
                                Logger logger)
            : base(database, eventAggregator)
        {
            _logger = logger;
        }

        // Photo selection has its own conditional writer. A metadata refresh or author
        // edit must never save an older selection over a newer user choice.
        protected override List<PropertyInfo> GetUpdateProperties() =>
            _updatePropertiesWithoutPosterChoice ??= base.GetUpdateProperties()
                .Where(property => property.Name != nameof(Author.SelectedPosterHash)).ToList();

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

        public bool UpdateSelectedPosterHash(int authorId, string hash, string expectedHash = null)
        {
            var timer = Stopwatch.StartNew();
            using var connection = _database.OpenConnection();
            var written = connection.Execute("UPDATE \"Authors\" SET \"SelectedPosterHash\" = @hash WHERE \"Id\" = @authorId " +
                "AND (@expectedHash IS NULL OR \"SelectedPosterHash\" = @expectedHash) " +
                "AND COALESCE(\"SelectedPosterHash\", '') <> COALESCE(@hash, '')", new { authorId, hash, expectedHash });
            _logger.Debug("Author photo selection: author {0}, {1} rows updated in {2} ms", authorId, written, timer.ElapsedMilliseconds);
            return written != 0;
        }

        public bool RemoveImageUrls(int authorId, IReadOnlyCollection<string> urls)
        {
            if (urls == null || urls.Count == 0) return false;
            var timer = Stopwatch.StartNew();
            using var connection = _database.OpenConnection();
            var read = 0;
            var removed = 0;
            var written = 0;
            try
            {
                var before = connection.QuerySingleOrDefault<string>("SELECT \"Images\" FROM \"Authors\" WHERE \"Id\" = @authorId", new { authorId });
                var images = before == null ? null : STJson.Deserialize<List<MediaCover.MediaCover>>(before);
                if (images == null) return false;
                read = images.Count;
                var kept = images.Where(image => image == null || !urls.Contains(image.Url)).ToList();
                removed = images.Count - kept.Count;
                if (removed == 0) return false;
                // Update only this field, and never overwrite a newer image list read during repair.
                written = connection.Execute("UPDATE \"Authors\" SET \"Images\" = @after WHERE \"Id\" = @authorId AND \"Images\" = @before",
                    new { authorId, before, after = STJson.ToJson(kept) });
                return written != 0;
            }
            finally
            {
                _logger.Debug("Author image cleanup: author {0}, {1} images read, {2} removals requested, {3} rows updated in {4} ms",
                    authorId, read, removed, written, timer.ElapsedMilliseconds);
            }
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
