using System;
using System.Collections.Generic;
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
        bool AuthorPathExists(string path);
        Author FindByName(string cleanName);
        Author FindByGoodreadsId(string goodreadsId);
        Author FindByHardcoverId(string hardcoverId);
        Author FindByAudnexusId(string audnexusId);
        Author FindByOpenLibraryId(string openLibraryId);
        Author FindByGoogleBooksId(string googleBooksAuthorId);
        List<int> GetAuthorIdsByMetadataProfileId(int metadataProfileId);
        Dictionary<int, string> AllAuthorPaths();
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

        public Author FindByName(string cleanName)
        {
            cleanName = cleanName.ToLowerInvariant();

            return Query(s => s.CleanName == cleanName).ExclusiveOrDefault();
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

        public Dictionary<int, string> AllAuthorPaths()
        {
            using (var conn = _database.OpenConnection())
            {
                var strSql = "SELECT \"Id\" AS \"Key\", \"Path\" AS \"Value\" FROM \"Authors\"";
                return conn.Query<KeyValuePair<int, string>>(strSql).ToDictionary(x => x.Key, x => x.Value);
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
