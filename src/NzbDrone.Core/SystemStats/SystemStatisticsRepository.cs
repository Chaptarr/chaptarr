using System;
using System.Linq;
using Dapper;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Books;

namespace NzbDrone.Core.SystemStats
{
    public class SystemStatisticsRepository : ISystemStatisticsRepository
    {
        private readonly IMainDatabase _database;

        public SystemStatisticsRepository(IMainDatabase database)
        {
            _database = database;
        }

        public SystemStatistics GetSystemStatistics(string mediaType)
        {
            var stats = new SystemStatistics();
            
            using (var conn = _database.OpenConnection())
            {
                // Determine media type filter
                int? mediaTypeFilter = null;
                if (mediaType?.ToLowerInvariant() == "audiobook")
                    mediaTypeFilter = (int)BookMediaType.Audiobook;
                else if (mediaType?.ToLowerInvariant() == "ebook")
                    mediaTypeFilter = (int)BookMediaType.Ebook;
                
                // Parameterize enum values to avoid magic numbers
                var audiobook = (int)BookMediaType.Audiobook;
                var ebook = (int)BookMediaType.Ebook;

                // Query 1: Book statistics (no JOINs for performance)
                var bookSql = @"
                    SELECT 
                        CAST(COUNT(*) AS INTEGER) as TotalBooks,
                        CAST(COUNT(DISTINCT ""AuthorId"") AS INTEGER) as AuthorCount,
                        CAST(COALESCE(SUM(CASE WHEN ""MediaType"" = @audiobook THEN 1 ELSE 0 END), 0) AS INTEGER) as AudiobookCount,
                        CAST(COALESCE(SUM(CASE WHEN ""MediaType"" = @ebook THEN 1 ELSE 0 END), 0) AS INTEGER) as EbookCount,
                        CAST(COALESCE(SUM(CASE 
                            WHEN ""MediaType"" = @audiobook AND ""AudiobookMonitored"" = @true THEN 1
                            WHEN ""MediaType"" = @ebook AND ""EbookMonitored"" = @true THEN 1
                            ELSE 0 
                        END), 0) AS INTEGER) as MonitoredBooks,
                        CAST(COALESCE(SUM(CASE WHEN ""MediaType"" = @audiobook AND ""AudiobookMonitored"" = @true THEN 1 ELSE 0 END), 0) AS INTEGER) as AudiobooksMonitored,
                        CAST(COALESCE(SUM(CASE WHEN ""MediaType"" = @ebook AND ""EbookMonitored"" = @true THEN 1 ELSE 0 END), 0) AS INTEGER) as EbooksMonitored
                    FROM ""Books""
                    WHERE (@mediaType IS NULL OR ""MediaType"" = @mediaType)";

                var bookStats = conn.QuerySingleOrDefault<SystemStatistics>(bookSql, new 
                { 
                    mediaType = mediaTypeFilter,
                    audiobook = audiobook,
                    ebook = ebook,
                    @true = _database.DatabaseType == DatabaseType.PostgreSQL ? (object)true : 1
                });

                if (bookStats != null)
                {
                    stats.TotalBooks = bookStats.TotalBooks;
                    stats.AuthorCount = bookStats.AuthorCount;
                    stats.AudiobookCount = bookStats.AudiobookCount;
                    stats.EbookCount = bookStats.EbookCount;
                    stats.MonitoredBooks = bookStats.MonitoredBooks;
                    stats.AudiobooksMonitored = bookStats.AudiobooksMonitored;
                    stats.EbooksMonitored = bookStats.EbooksMonitored;
                }

                // Query 2: File statistics (join through Editions)
                var fileSql = @"
                    SELECT 
                        CAST(COUNT(bf.""Id"") AS INTEGER) as FileCount,
                        COALESCE(SUM(bf.""Size""), 0) as TotalFileSize
                    FROM ""BookFiles"" bf
                    INNER JOIN ""Editions"" e ON bf.""EditionId"" = e.""Id""
                    INNER JOIN ""Books"" b ON e.""BookId"" = b.""Id""
                    WHERE (@mediaType IS NULL OR b.""MediaType"" = @mediaType)";

                var fileStats = conn.QuerySingleOrDefault<SystemStatistics>(fileSql, new 
                { 
                    mediaType = mediaTypeFilter 
                });

                if (fileStats != null)
                {
                    stats.FileCount = fileStats.FileCount;
                    stats.TotalFileSize = fileStats.TotalFileSize;
                }
            }

            return stats;
        }
    }
}
