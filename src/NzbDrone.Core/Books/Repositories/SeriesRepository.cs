using System;
using System.Collections.Generic;
using System.Linq;
using NLog;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Messaging.Events;

namespace NzbDrone.Core.Books
{
    public interface ISeriesRepository : IBasicRepository<Series>
    {
        Series FindById(string providerIdOrLocalId);
        Series FindById(string providerIdOrLocalId, BookMediaType mediaType);
        List<Series> FindById(List<string> providerIds);
        List<Series> FindById(List<string> providerIds, BookMediaType mediaType);
        List<Series> GetByAuthorId(int authorId);
        List<Series> GetAllSeriesWithoutBooks();
    }

    public class SeriesRepository : BasicRepository<Series>, ISeriesRepository
    {
        private readonly ISeriesBookLinkRepository _linkRepository;
        private readonly Logger _logger;

        public SeriesRepository(IMainDatabase database, IEventAggregator eventAggregator, ISeriesBookLinkRepository linkRepository, Logger logger)
            : base(database, eventAggregator)
        {
            _linkRepository = linkRepository;
            _logger = logger;
        }

        public Series FindById(string providerIdOrLocalId)
        {
            // Try to find by any provider ID or local ID
            // With dual instances, we might have multiple series with same provider IDs
            // Use custom query to exclude Books column which causes JSON parsing errors
            var sql = @"SELECT ""Id"", ""Title"", ""TitleSlug"", ""Description"", ""Numbered"", ""WorkCount"", ""PrimaryWorkCount"",
                           ""GoodreadsSeriesId"", ""HardcoverSeriesId"", ""OpenLibrarySeriesId"", ""AmazonSeriesAsin"", ""SeriesType"",
                           ""ParentSeriesId"", ""TotalBooks"", ""PrimaryBooks"", ""Narrator"", ""BaseSeriesId"",
                           ""MediaType"", ""InstanceNumber"", ""PreferredNarratorId"",
                           ""ProviderUrls"", ""LastUpdated"", ""Links""
                           FROM ""Series""
                           WHERE ""HardcoverSeriesId"" = @providerId
                           OR ""GoodreadsSeriesId"" = @providerId
                       OR ""OpenLibrarySeriesId"" = @providerId
                       OR ""AmazonSeriesAsin"" = @providerId
                       LIMIT 1";

            return _database.Query<Series>(sql, new { providerId = providerIdOrLocalId }).SingleOrDefault();
        }

        public Series FindById(string providerIdOrLocalId, BookMediaType mediaType)
        {
            // Chaptarr has one series row per media type. Provider-id lookup is only unique
            // when scoped to media type; media-aware callers should not use the legacy single-row overload.
            var sql = @"SELECT ""Id"", ""Title"", ""TitleSlug"", ""Description"", ""Numbered"", ""WorkCount"", ""PrimaryWorkCount"",
                           ""GoodreadsSeriesId"", ""HardcoverSeriesId"", ""OpenLibrarySeriesId"", ""AmazonSeriesAsin"", ""SeriesType"",
                           ""ParentSeriesId"", ""TotalBooks"", ""PrimaryBooks"", ""Narrator"", ""BaseSeriesId"",
                           ""MediaType"", ""InstanceNumber"", ""PreferredNarratorId"",
                           ""ProviderUrls"", ""LastUpdated"", ""Links""
                           FROM ""Series""
                           WHERE ""MediaType"" = @mediaType
                           AND ""PreferredNarratorId"" IS NULL
                           AND (""Narrator"" IS NULL OR TRIM(""Narrator"") = '')
                           AND (""HardcoverSeriesId"" = @providerId
                           OR ""GoodreadsSeriesId"" = @providerId
                           OR ""OpenLibrarySeriesId"" = @providerId
                           OR ""AmazonSeriesAsin"" = @providerId)";

            return _database.Query<Series>(sql, new { providerId = providerIdOrLocalId, mediaType = (int)mediaType }).SingleOrDefault();
        }

        public List<Series> FindById(List<string> providerIds)
        {
            if (!providerIds.Any())
            {
                return new List<Series>();
            }

            // Use custom query to exclude Books column which causes JSON parsing errors
            var providerIdsArray = providerIds.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct().ToArray();
            if (providerIdsArray.Length == 0)
            {
                return new List<Series>();
            }

            var isPostgres = _database.DatabaseType == DatabaseType.PostgreSQL;
            var isSqlite = _database.DatabaseType == DatabaseType.SQLite;
            var sql = isPostgres
                ? @"SELECT ""Id"", ""Title"", ""TitleSlug"", ""Description"", ""Numbered"", ""WorkCount"", ""PrimaryWorkCount"",
                           ""GoodreadsSeriesId"", ""HardcoverSeriesId"", ""OpenLibrarySeriesId"", ""AmazonSeriesAsin"", ""SeriesType"",
                           ""ParentSeriesId"", ""TotalBooks"", ""PrimaryBooks"", ""Narrator"", ""BaseSeriesId"",
                           ""MediaType"", ""InstanceNumber"", ""PreferredNarratorId"",
                           ""ProviderUrls"", ""LastUpdated"", ""Links""
                           FROM ""Series""
                           WHERE ""HardcoverSeriesId"" = ANY(@providerIds)
                           OR ""GoodreadsSeriesId"" = ANY(@providerIds)
                           OR ""OpenLibrarySeriesId"" = ANY(@providerIds)
                           OR ""AmazonSeriesAsin"" = ANY(@providerIds)"
                : @"SELECT ""Id"", ""Title"", ""TitleSlug"", ""Description"", ""Numbered"", ""WorkCount"", ""PrimaryWorkCount"",
                           ""GoodreadsSeriesId"", ""HardcoverSeriesId"", ""OpenLibrarySeriesId"", ""AmazonSeriesAsin"", ""SeriesType"",
                           ""ParentSeriesId"", ""TotalBooks"", ""PrimaryBooks"", ""Narrator"", ""BaseSeriesId"",
                           ""MediaType"", ""InstanceNumber"", ""PreferredNarratorId"",
                           ""ProviderUrls"", ""LastUpdated"", ""Links""
                           FROM ""Series""
                           WHERE ""HardcoverSeriesId"" IN @providerIds
                           OR ""GoodreadsSeriesId"" IN @providerIds
                       OR ""OpenLibrarySeriesId"" IN @providerIds
                       OR ""AmazonSeriesAsin"" IN @providerIds";

            if (isSqlite && providerIdsArray.Length > SqliteVariableLimit.MaxParameters)
            {
                var series = new List<Series>();
                foreach (var batch in providerIdsArray.Chunk(SqliteVariableLimit.MaxParameters))
                {
                    series.AddRange(_database.Query<Series>(sql, new { providerIds = batch.ToArray() }));
                }

                return series.DistinctBy(s => s.Id).ToList();
            }

            return _database.Query<Series>(sql, new { providerIds = providerIdsArray }).ToList();
        }

        public List<Series> FindById(List<string> providerIds, BookMediaType mediaType)
        {
            if (!providerIds.Any())
            {
                return new List<Series>();
            }

            var providerIdsArray = providerIds.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct().ToArray();
            if (providerIdsArray.Length == 0)
            {
                return new List<Series>();
            }

            var isPostgres = _database.DatabaseType == DatabaseType.PostgreSQL;
            var isSqlite = _database.DatabaseType == DatabaseType.SQLite;
            var sql = isPostgres
                ? @"SELECT ""Id"", ""Title"", ""TitleSlug"", ""Description"", ""Numbered"", ""WorkCount"", ""PrimaryWorkCount"",
                           ""GoodreadsSeriesId"", ""HardcoverSeriesId"", ""OpenLibrarySeriesId"", ""AmazonSeriesAsin"", ""SeriesType"",
                           ""ParentSeriesId"", ""TotalBooks"", ""PrimaryBooks"", ""Narrator"", ""BaseSeriesId"",
                           ""MediaType"", ""InstanceNumber"", ""PreferredNarratorId"",
                           ""ProviderUrls"", ""LastUpdated"", ""Links""
                           FROM ""Series""
                           WHERE ""MediaType"" = @mediaType
                           AND ""PreferredNarratorId"" IS NULL
                           AND (""Narrator"" IS NULL OR TRIM(""Narrator"") = '')
                           AND (""HardcoverSeriesId"" = ANY(@providerIds)
                           OR ""GoodreadsSeriesId"" = ANY(@providerIds)
                           OR ""OpenLibrarySeriesId"" = ANY(@providerIds)
                           OR ""AmazonSeriesAsin"" = ANY(@providerIds))"
                : @"SELECT ""Id"", ""Title"", ""TitleSlug"", ""Description"", ""Numbered"", ""WorkCount"", ""PrimaryWorkCount"",
                           ""GoodreadsSeriesId"", ""HardcoverSeriesId"", ""OpenLibrarySeriesId"", ""AmazonSeriesAsin"", ""SeriesType"",
                           ""ParentSeriesId"", ""TotalBooks"", ""PrimaryBooks"", ""Narrator"", ""BaseSeriesId"",
                           ""MediaType"", ""InstanceNumber"", ""PreferredNarratorId"",
                           ""ProviderUrls"", ""LastUpdated"", ""Links""
                           FROM ""Series""
                           WHERE ""MediaType"" = @mediaType
                           AND ""PreferredNarratorId"" IS NULL
                           AND (""Narrator"" IS NULL OR TRIM(""Narrator"") = '')
                           AND (""HardcoverSeriesId"" IN @providerIds
                           OR ""GoodreadsSeriesId"" IN @providerIds
                           OR ""OpenLibrarySeriesId"" IN @providerIds
                           OR ""AmazonSeriesAsin"" IN @providerIds)";

            if (isSqlite && providerIdsArray.Length > SqliteVariableLimit.MaxParameters)
            {
                var series = new List<Series>();
                foreach (var batch in providerIdsArray.Chunk(SqliteVariableLimit.MaxParameters))
                {
                    series.AddRange(_database.Query<Series>(sql, new { providerIds = batch.ToArray(), mediaType = (int)mediaType }));
                }

                return series.DistinctBy(s => s.Id).ToList();
            }

            return _database.Query<Series>(sql, new { providerIds = providerIdsArray, mediaType = (int)mediaType }).ToList();
        }

        public List<Series> GetByAuthorId(int authorId)
        {
            _logger.Debug($"GetByAuthorId called with authorId: {authorId}");

            // Primary path: Series are linked to books via SeriesBookLink (Readarr-style).
            var linkedSeries = QueryDistinct(Builder()
                .Join<Series, SeriesBookLink>((s, sbl) => s.Id == sbl.SeriesId)
                .Join<SeriesBookLink, Book>((sbl, b) => sbl.BookId == b.Id)
                .Where<Book>(b => b.AuthorId == authorId));

            if (!linkedSeries.Any())
            {
                _logger.Debug($"No series found for author {authorId} via SeriesBookLink");
                return new List<Series>();
            }

            _logger.Debug($"Found {linkedSeries.Count} series for author {authorId} via SeriesBookLink");
            PopulateSeriesLinks(linkedSeries);
            return linkedSeries;
        }

        private void PopulateSeriesLinks(List<Series> series)
        {
            if (!series.Any())
            {
                return;
            }

            // Batch load all links for these series
            // Use a simple loop since we can't batch query across repositories
            foreach (var s in series)
            {
                s.LinkItems = _linkRepository.GetLinksBySeries(s.Id);
            }
        }

        public List<Series> GetAllSeriesWithoutBooks()
        {
            // Query all series without the Books column to avoid JSON parsing errors
            var sql = @"SELECT ""Id"", ""Title"", ""TitleSlug"", ""Description"", ""Numbered"", ""WorkCount"", ""PrimaryWorkCount"",
                           ""GoodreadsSeriesId"", ""HardcoverSeriesId"", ""OpenLibrarySeriesId"", ""AmazonSeriesAsin"", ""SeriesType"",
                           ""ParentSeriesId"", ""TotalBooks"", ""PrimaryBooks"", ""Narrator"", ""BaseSeriesId"",
                           ""MediaType"", ""InstanceNumber"", ""PreferredNarratorId"",
                           ""ProviderUrls"", ""LastUpdated"", ""Links""
                           FROM ""Series""";

            return _database.Query<Series>(sql).ToList();
        }
    }
}
